// Tests of the CI scripts: `node --test .github/scripts`. No dependency.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { delimiter, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
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

// ---- the shell scripts (run B.8), run as CI runs them: bash, in a throwaway repository or with a fake docker ----------

const scripts = dirname(fileURLToPath(import.meta.url));
const root = join(scripts, '..', '..');
const sh = (args, options) => spawnSync('bash', args, { encoding: 'utf8', ...options });

/** A repository with one commit per entry: its author, and whether it is signed off. Returns the base and head. */
function repository(commits) {
  const dir = mkdtempSync(join(tmpdir(), 'inkpact-dco-'));
  const git = (...args) => {
    const result = spawnSync('git', args, { cwd: dir, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim();
  };
  git('init', '-q');
  git('-c', 'user.name=Base', '-c', 'user.email=base@example.com', 'commit', '-q', '--allow-empty', '-m', 'chore: base', '-s');
  const base = git('rev-parse', 'HEAD');
  for (const [i, { name, email, signed }] of commits.entries()) {
    git('-c', `user.name=${name}`, '-c', `user.email=${email}`, 'commit', '-q', '--allow-empty', '-m', `chore: ${i}`, ...(signed ? ['-s'] : []));
  }
  return { dir, base, head: git('rev-parse', 'HEAD') };
}

const bot = 'inkpact-release[bot]';
const botCommit = { name: bot, email: `123+${bot}@users.noreply.github.com`, signed: false };
const person = (signed) => ({ name: 'Jos', email: 'jos@example.com', signed });

function dco(commits, botAuthor = '') {
  const { dir, base, head } = repository(commits);
  try {
    return sh([join(scripts, 'check-dco.sh'), base, head], { cwd: dir, env: { ...process.env, BOT_AUTHOR: botAuthor } });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test('dco: signed-off commits pass, one without a sign-off fails', () => {
  assert.equal(dco([person(true), person(true)]).status, 0);
  const result = dco([person(true), person(false)]);
  assert.equal(result.status, 1);
  assert.match(result.stdout, /not signed off by its author \(Jos <jos@example\.com>\)/);
});

test('dco: on the release pull request, only the release bot\'s own commits are skipped', () => {
  assert.equal(dco([botCommit], bot).status, 0);
  // Someone else's commit pushed onto the release branch is checked as usual.
  assert.equal(dco([botCommit, person(false)], bot).status, 1);
  assert.equal(dco([botCommit, person(true)], bot).status, 0);
});

test('dco: the bot is known by its name and its noreply address both, and only when named', () => {
  assert.equal(dco([botCommit]).status, 1);
  assert.equal(dco([{ ...botCommit, email: 'jos@example.com' }], bot).status, 1);
  assert.equal(dco([{ ...botCommit, email: `123+other[bot]@users.noreply.github.com` }], bot).status, 1);
  assert.equal(dco([{ ...botCommit, name: 'Jos' }], bot).status, 1);
  assert.equal(dco([{ name: 'jos', email: '1+jos@users.noreply.github.com', signed: false }], 'jos').status, 1);
});

/** image-version.sh with a docker that has no `latest` (or has it), its outputs as a map. */
function imageVersion(env, latestExists = false) {
  const bin = mkdtempSync(join(tmpdir(), 'inkpact-docker-'));
  const out = join(bin, 'output');
  writeFileSync(join(bin, 'docker'), `#!/usr/bin/env bash\nexit ${latestExists ? 0 : 1}\n`, { mode: 0o755 });
  try {
    const result = sh([join(scripts, 'image-version.sh')], {
      cwd: root,
      env: { ...process.env, PATH: `${bin}${delimiter}${process.env.PATH}`, IMAGE: 'ghcr.io/gotgeeks/inkpact', SHA: 'abcdef1234567', GITHUB_OUTPUT: out, ...env },
    });
    if (result.status !== 0) return { status: result.status, stdout: result.stdout };
    const text = readFileSync(out, 'utf8');
    const values = Object.fromEntries([...text.matchAll(/^(\w+)=(.*)$/gm)].map((m) => [m[1], m[2]]));
    values.tags = text.split('tags<<TAGS\n')[1].split('\nTAGS')[0].split('\n');
    return { status: 0, ...values };
  } finally {
    rmSync(bin, { recursive: true, force: true });
  }
}

test('image version: a merge to main publishes edge, stamped with the next version', () => {
  const manifest = JSON.parse(readFileSync(join(root, '.release-please-manifest.json'), 'utf8'))['.'];
  const merge = imageVersion({ EVENT: 'push', REF: 'refs/heads/main' });
  assert.equal(merge.version, `${manifest}+edge.abcdef1`);
  assert.equal(merge.publish, 'true');
  assert.equal(merge.release, 'false');
  assert.deepEqual(merge.tags, ['ghcr.io/gotgeeks/inkpact:edge', 'ghcr.io/gotgeeks/inkpact:sha-abcdef1']);
  assert.equal(merge.from, 'ghcr.io/gotgeeks/inkpact:edge');
  assert.equal(imageVersion({ EVENT: 'push', REF: 'refs/heads/main' }, true).from, 'ghcr.io/gotgeeks/inkpact:latest');
});

test('image version: a release publishes x.y.z, x.y and latest', () => {
  const release = imageVersion({ EVENT: 'push', REF: 'refs/heads/main', RELEASE: '0.2.1' });
  assert.equal(release.version, '0.2.1');
  assert.equal(release.publish, 'true');
  assert.deepEqual(release.tags, ['ghcr.io/gotgeeks/inkpact:0.2.1', 'ghcr.io/gotgeeks/inkpact:0.2', 'ghcr.io/gotgeeks/inkpact:latest']);
  assert.equal(imageVersion({ EVENT: 'push', REF: 'refs/heads/main', RELEASE: '0.2.1;x' }).status, 1);
});

test('image version: only main publishes edge; a pull request and another branch build and check', () => {
  assert.equal(imageVersion({ EVENT: 'workflow_dispatch', REF: 'refs/heads/main', PUBLISH_INPUT: 'true' }).publish, 'true');
  assert.equal(imageVersion({ EVENT: 'workflow_dispatch', REF: 'refs/heads/main', PUBLISH_INPUT: 'false' }).publish, 'false');
  assert.equal(imageVersion({ EVENT: 'workflow_dispatch', REF: 'refs/heads/feat/x', PUBLISH_INPUT: 'true' }).publish, 'false');
  assert.equal(imageVersion({ EVENT: 'pull_request', REF: 'refs/pull/1/merge' }).publish, 'false');
});
