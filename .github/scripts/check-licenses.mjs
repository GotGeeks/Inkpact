// Checks the licence of every npm and NuGet package against .github/license-policy.json.
// Plain Node, no dependency. Run it with `pnpm check:licenses` (the NuGet packages must be restored first).
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

/** Splits an SPDX expression into tokens: names, AND, OR, WITH and parentheses. */
function tokenize(expression) {
  return expression.match(/\(|\)|[^\s()]+/g) ?? [];
}

/**
 * Does the SPDX expression only use allowed licences? OR passes when one side does, AND when both do.
 * A `WITH` exception or a `+` suffix is a different licence: it must itself be in the allowed list.
 */
export function isAllowed(expression, allowed) {
  const tokens = tokenize(expression ?? '');
  if (tokens.length === 0) return false;
  const set = new Set(allowed);
  let pos = 0;

  const parseOr = () => {
    let ok = parseAnd();
    while (tokens[pos] === 'OR') {
      pos++;
      const right = parseAnd();
      ok = ok || right;
    }
    return ok;
  };
  const parseAnd = () => {
    let ok = parseTerm();
    while (tokens[pos] === 'AND') {
      pos++;
      const right = parseTerm();
      ok = ok && right;
    }
    return ok;
  };
  const parseTerm = () => {
    if (tokens[pos] === '(') {
      pos++;
      const ok = parseOr();
      if (tokens[pos] !== ')') throw new Error(`Unbalanced parentheses in "${expression}"`);
      pos++;
      return ok;
    }
    let name = tokens[pos++];
    if (name === undefined || name === 'AND' || name === 'OR' || name === 'WITH' || name === ')') {
      throw new Error(`Cannot read the licence expression "${expression}"`);
    }
    if (tokens[pos] === 'WITH') {
      name = `${name} WITH ${tokens[pos + 1]}`;
      pos += 2;
    }
    return set.has(name);
  };

  try {
    const ok = parseOr();
    return ok && pos === tokens.length;
  } catch {
    return false;
  }
}

/**
 * @param packages {{ source: 'npm'|'nuget', name: string, version: string, license: string|null }[]}
 * @param policy the parsed license-policy.json
 * @returns the packages that fail, each with the reason
 */
export function findViolations(packages, policy) {
  const violations = [];
  for (const pkg of packages) {
    const entry = policy.packages?.[`${pkg.source}:${pkg.name}`];
    const license = entry ? entry.license : pkg.license;
    if (!license) {
      violations.push({ ...pkg, reason: 'no licence found' });
    } else if (!isAllowed(license, policy.allowed)) {
      violations.push({ ...pkg, license, reason: `licence "${license}" is not in the policy` });
    }
  }
  return violations;
}

/** `pnpm -r licenses list --json` groups packages by licence. */
export function parsePnpmLicenses(json) {
  const result = [];
  for (const [license, list] of Object.entries(json)) {
    for (const pkg of list) {
      for (const version of pkg.versions ?? ['?']) {
        result.push({ source: 'npm', name: pkg.name, version, license: license === 'Unknown' ? null : license });
      }
    }
  }
  return result;
}

/** The licence a .nuspec declares as an SPDX expression. A file or only a URL is not an expression: null. */
export function parseNuspecLicense(xml) {
  const expression = /<license\s+type="expression"\s*>([^<]+)<\/license>/i.exec(xml);
  return expression ? expression[1].trim() : null;
}

/** `dotnet list package --include-transitive --format json`: the distinct packages of all projects. */
export function parseDotnetPackages(json) {
  const seen = new Map();
  for (const project of json.projects ?? []) {
    for (const framework of project.frameworks ?? []) {
      for (const pkg of [...(framework.topLevelPackages ?? []), ...(framework.transitivePackages ?? [])]) {
        seen.set(`${pkg.id}@${pkg.resolvedVersion}`, { name: pkg.id, version: pkg.resolvedVersion });
      }
    }
  }
  return [...seen.values()];
}

function collect() {
  const npm = parsePnpmLicenses(
    JSON.parse(
      execFileSync('pnpm', ['-r', 'licenses', 'list', '--json'], {
        cwd: root,
        encoding: 'utf8',
        maxBuffer: 1 << 28,
        shell: process.platform === 'win32',
      }),
    ),
  );
  const dotnet = parseDotnetPackages(
    JSON.parse(
      execFileSync('dotnet', ['list', 'server', 'package', '--include-transitive', '--format', 'json'], {
        cwd: root,
        encoding: 'utf8',
        maxBuffer: 1 << 28,
      }),
    ),
  );
  const cache = process.env.NUGET_PACKAGES ?? join(homedir(), '.nuget', 'packages');
  const nuget = dotnet.map((pkg) => {
    const id = pkg.name.toLowerCase();
    const nuspec = join(cache, id, pkg.version.toLowerCase(), `${id}.nuspec`);
    return {
      source: 'nuget',
      ...pkg,
      license: existsSync(nuspec) ? parseNuspecLicense(readFileSync(nuspec, 'utf8')) : null,
    };
  });
  return [...npm, ...nuget];
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const policy = JSON.parse(readFileSync(join(root, '.github', 'license-policy.json'), 'utf8'));
  const packages = collect();
  const violations = findViolations(packages, policy);
  if (violations.length > 0) {
    for (const v of violations) {
      console.error(`${v.source}: ${v.name}@${v.version}: ${v.reason}`);
    }
    console.error(`\n${violations.length} package(s) fail the licence policy (.github/license-policy.json).`);
    process.exit(1);
  }
  console.log(`${packages.length} packages checked, all licences allowed.`);
}
