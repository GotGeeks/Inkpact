// Decides whether a Dependabot pull request may be merged without the maintainer: only a patch update of web
// dev tooling, whose whole effect the checks of a pull request test (lint, types, Vitest, commitlint).
// Called by .github/workflows/auto-merge.yml with the outputs of dependabot/fetch-metadata.
import { appendFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// The Dependabot groups of .github/dependabot.yml that hold only tooling.
export const AUTOMERGE_GROUPS = ['web-lint', 'web-types', 'web-unit-tests', 'commitlint'];

/**
 * @param meta {{ ecosystem: string, updateType: string, group: string }}
 *   ecosystem is fetch-metadata's `package-ecosystem` (npm_and_yarn, nuget, github_actions, ...),
 *   updateType its `update-type` (version-update:semver-patch, ...), group its `dependency-group`.
 */
export function shouldAutoMerge({ ecosystem, updateType, group }) {
  return (
    ecosystem === 'npm_and_yarn' && updateType === 'version-update:semver-patch' && AUTOMERGE_GROUPS.includes(group)
  );
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const merge = shouldAutoMerge({
    ecosystem: process.env.ECOSYSTEM ?? '',
    updateType: process.env.UPDATE_TYPE ?? '',
    group: process.env.GROUP ?? '',
  });
  console.log(merge ? 'Patch of web dev tooling: auto-merge.' : 'Not auto-merged: left for the maintainer.');
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `merge=${merge}\n`);
}
