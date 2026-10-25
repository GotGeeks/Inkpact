# Contributing to Inkpact

Inkpact is a self-hosted notes app with real-time collaboration, licensed AGPL-3.0 with no paid features. Contributions are welcome. Please open an issue before starting anything large, so we can agree on the approach first.

Read `docs/architecture.md` for the design and `CLAUDE.md` for the conventions; they apply to people as well as to the coding assistant.

## Setup

You need .NET 10, Node 24 with Corepack enabled (`corepack enable`), and Docker for the server integration tests.

```bash
pnpm install --frozen-lockfile
pnpm --filter web dev
dotnet build server
```

Use pnpm only, never npm or yarn. Ask in the issue before adding a dependency, and say why it is needed.

## Before you open a pull request

```bash
pnpm --filter web lint
pnpm --filter web typecheck
pnpm --filter web test
dotnet format server --verify-no-changes
dotnet test server
```

CI runs the same checks. Keep a pull request to one testable slice, with its tests and its translations.

## Rules that are not negotiable

- New logic comes with tests.
- No hard-coded user-facing text: add keys to both `en.json` and `nl.json`.
- The Content Security Policy stays strict: no inline scripts or styles, no `eval`, no unsanitised `v-html`.
- Styling is Tailwind with the theme tokens; no arbitrary colours.

## Commits

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/), for example `feat(editor): add slash menu`. Every commit must carry a Developer Certificate of Origin sign-off:

```bash
git commit -s -m "fix(sync): close dead sockets"
```

The `-s` flag adds `Signed-off-by: Your Name <you@example.com>`, which certifies that you wrote the change or have the right to submit it under the project license ([developercertificate.org](https://developercertificate.org/)). CI checks both the format and the sign-off.
