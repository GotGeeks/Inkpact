// Reads `dotnet list server package --vulnerable --include-transitive --format json` and fails on a known
// vulnerability: moderate or worse in the shipped projects, high or worse in the test project.
// Plain Node, no dependency.
import { execFileSync } from 'node:child_process';
import { basename, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const rank = { low: 1, moderate: 2, high: 3, critical: 4 };
const TEST_PROJECT = /\.Tests$/;

/** @returns the findings that break the rule, as { project, package, version, severity, url } */
export function findVulnerabilities(json) {
  const found = [];
  for (const project of json.projects ?? []) {
    const name = basename((project.path ?? '').replaceAll('\\', '/'), '.csproj');
    const threshold = TEST_PROJECT.test(name) ? rank.high : rank.moderate;
    for (const framework of project.frameworks ?? []) {
      for (const pkg of [...(framework.topLevelPackages ?? []), ...(framework.transitivePackages ?? [])]) {
        for (const vuln of pkg.vulnerabilities ?? []) {
          const severity = String(vuln.severity ?? '').toLowerCase();
          // An unknown severity counts as the worst: better a false alarm than a miss.
          if ((rank[severity] ?? rank.critical) >= threshold) {
            found.push({
              project: name,
              package: pkg.id,
              version: pkg.resolvedVersion,
              severity: vuln.severity,
              url: vuln.advisoryurl,
            });
          }
        }
      }
    }
  }
  return found;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
  const json = JSON.parse(
    execFileSync('dotnet', ['list', 'server', 'package', '--vulnerable', '--include-transitive', '--format', 'json'], {
      cwd: root,
      encoding: 'utf8',
      maxBuffer: 1 << 28,
    }),
  );
  const found = findVulnerabilities(json);
  for (const f of found) {
    console.error(`${f.project}: ${f.package}@${f.version} has a ${f.severity} advisory: ${f.url}`);
  }
  if (found.length > 0) process.exit(1);
  console.log('No NuGet package with a known vulnerability above the threshold.');
}
