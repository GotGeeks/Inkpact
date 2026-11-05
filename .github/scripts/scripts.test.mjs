// Tests of the CI scripts: `node --test .github/scripts`. No dependency.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { shouldAutoMerge } from './automerge.mjs';
import { findViolations, isAllowed, parseDotnetPackages, parseNuspecLicense, parsePnpmLicenses } from './check-licenses.mjs';
import { findVulnerabilities } from './nuget-audit.mjs';

const allowed = ['MIT', 'Apache-2.0', 'MPL-2.0', 'ISC'];
const policy = {
  allowed,
  packages: { 'nuget:xunit.abstractions': { license: 'Apache-2.0', reason: 'licence URL only' } },
};

test('licence: an allowed licence passes, an unknown one fails', () => {
  assert.equal(isAllowed('MIT', allowed), true);
  assert.equal(isAllowed('GPL-2.0-only', allowed), false);
  assert.equal(isAllowed('SSPL-1.0', allowed), false);
});

test('licence: OR passes when one side does, AND when both do', () => {
  assert.equal(isAllowed('(MPL-2.0 OR Apache-2.0)', allowed), true);
  assert.equal(isAllowed('GPL-2.0-only OR MIT', allowed), true);
  assert.equal(isAllowed('GPL-2.0-only OR BUSL-1.1', allowed), false);
  assert.equal(isAllowed('MIT AND ISC', allowed), true);
  assert.equal(isAllowed('MIT AND GPL-2.0-only', allowed), false);
  assert.equal(isAllowed('MIT AND (GPL-2.0-only OR ISC)', allowed), true);
});

test('licence: an exception, a missing licence and a broken expression fail', () => {
  assert.equal(isAllowed('MIT WITH Classpath-exception-2.0', allowed), false);
  assert.equal(isAllowed('', allowed), false);
  assert.equal(isAllowed(null, allowed), false);
  assert.equal(isAllowed('(MIT', allowed), false);
  assert.equal(isAllowed('MIT OR', allowed), false);
});

test('licence: a per-package entry stands in for a missing licence', () => {
  const packages = [
    { source: 'nuget', name: 'xunit.abstractions', version: '2.0.3', license: null },
    { source: 'nuget', name: 'other', version: '1.0.0', license: null },
  ];
  const violations = findViolations(packages, policy);
  assert.equal(violations.length, 1);
  assert.equal(violations[0].name, 'other');
  assert.match(violations[0].reason, /no licence/);
});

test('licence: a disallowed licence is reported with the package, version and licence', () => {
  const [v] = findViolations([{ source: 'npm', name: 'evil', version: '2.1.0', license: 'BUSL-1.1' }], policy);
  assert.equal(v.name, 'evil');
  assert.equal(v.version, '2.1.0');
  assert.equal(v.license, 'BUSL-1.1');
});

test('licence: pnpm output is flattened, "Unknown" counts as missing', () => {
  const parsed = parsePnpmLicenses({
    MIT: [{ name: 'a', versions: ['1.0.0', '2.0.0'] }],
    Unknown: [{ name: 'b', versions: ['3.0.0'] }],
  });
  assert.deepEqual(
    parsed.map((p) => [p.name, p.version, p.license]),
    [
      ['a', '1.0.0', 'MIT'],
      ['a', '2.0.0', 'MIT'],
      ['b', '3.0.0', null],
    ],
  );
});

test('licence: a nuspec gives an expression, but a bare URL gives nothing', () => {
  assert.equal(parseNuspecLicense('<package><metadata><license type="expression">MIT</license></metadata></package>'), 'MIT');
  assert.equal(parseNuspecLicense('<package><metadata><licenseUrl>https://example.com/l</licenseUrl></metadata></package>'), null);
});

test('licence: dotnet packages are de-duplicated across projects and frameworks', () => {
  const pkg = { id: 'Shouldly', resolvedVersion: '4.3.0' };
  const framework = { topLevelPackages: [pkg], transitivePackages: [{ id: 'X', resolvedVersion: '1.0.0' }] };
  const parsed = parseDotnetPackages({ projects: [{ frameworks: [framework] }, { frameworks: [framework] }] });
  assert.equal(parsed.length, 2);
});

const project = (path, severity) => ({
  path,
  frameworks: [
    {
      topLevelPackages: [
        { id: 'Some.Package', resolvedVersion: '1.0.0', vulnerabilities: [{ severity, advisoryurl: 'https://github.com/advisories/x' }] },
      ],
    },
  ],
});

test('nuget: a moderate advisory in the test project passes', () => {
  assert.deepEqual(findVulnerabilities({ projects: [project('C:/r/server/tests/Inkpact.Api.Tests/Inkpact.Api.Tests.csproj', 'Moderate')] }), []);
});

test('nuget: a moderate advisory in Inkpact.Api fails', () => {
  const found = findVulnerabilities({ projects: [project('C:/r/server/src/Inkpact.Api/Inkpact.Api.csproj', 'Moderate')] });
  assert.equal(found.length, 1);
  assert.equal(found[0].project, 'Inkpact.Api');
  assert.equal(found[0].package, 'Some.Package');
});

test('nuget: a low advisory passes everywhere, a high one fails in the test project too', () => {
  assert.deepEqual(findVulnerabilities({ projects: [project('C:/r/server/src/Inkpact.Data/Inkpact.Data.csproj', 'Low')] }), []);
  assert.equal(findVulnerabilities({ projects: [project('C:\\r\\server\\tests\\Inkpact.Api.Tests\\Inkpact.Api.Tests.csproj', 'High')] }).length, 1);
});

test('nuget: an unknown severity counts as the worst', () => {
  assert.equal(findVulnerabilities({ projects: [project('C:/r/server/tests/T.Tests/T.Tests.csproj', 'Strange')] }).length, 1);
});

const npm = 'npm_and_yarn';
const patch = 'version-update:semver-patch';

test('automerge: a patch in each tooling group merges', () => {
  for (const group of ['web-lint', 'web-types', 'web-unit-tests', 'commitlint']) {
    assert.equal(shouldAutoMerge({ ecosystem: npm, updateType: patch, group }), true, group);
  }
});

test('automerge: yjs, playwright and other groups wait', () => {
  for (const group of ['yjs', 'tiptap', 'vue', 'vite', 'playwright', '']) {
    assert.equal(shouldAutoMerge({ ecosystem: npm, updateType: patch, group }), false, group);
  }
});

test('automerge: a minor or major in a tooling group waits', () => {
  assert.equal(shouldAutoMerge({ ecosystem: npm, updateType: 'version-update:semver-minor', group: 'web-lint' }), false);
  assert.equal(shouldAutoMerge({ ecosystem: npm, updateType: 'version-update:semver-major', group: 'web-lint' }), false);
});

test('automerge: a NuGet patch and a GitHub Actions patch wait', () => {
  assert.equal(shouldAutoMerge({ ecosystem: 'nuget', updateType: patch, group: 'test-framework' }), false);
  assert.equal(shouldAutoMerge({ ecosystem: 'github_actions', updateType: patch, group: 'actions' }), false);
  assert.equal(shouldAutoMerge({ ecosystem: 'github_actions', updateType: patch, group: 'web-lint' }), false);
});
