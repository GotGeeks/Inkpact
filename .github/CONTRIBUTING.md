# Contributing to Inkpact

Inkpact is a self-hosted notes app with real-time collaboration, licensed AGPL-3.0 with no paid features. Contributions are welcome. Please open an issue before starting anything large, so we can agree on the approach first.

Read `docs/architecture.md` for the design and `CLAUDE.md` for the conventions; they apply to people as well as to the coding assistant.

## Setup

You need .NET 10, Node 26 (Corepack no longer ships with it: install pnpm with `npm install --global pnpm`; the version in `packageManager` is then used automatically), and Docker for the server integration tests.

```bash
pnpm install --frozen-lockfile
pnpm --filter web dev
dotnet tool restore   # installs dotnet-ef, for migrations
dotnet build server
```

Run `pnpm install` before `dotnet test`: the server tests start the real Yjs sidecar (`server/sidecar`), a Node process that needs its dependencies.

The .NET SDK is pinned to an exact version in `global.json`, so every machine and CI build the same way (and so a broken new release cannot stop the build). Dependabot opens a pull request when a newer SDK is out; CI checks that the build works with it, then you merge it. The `dotnet/sdk` image in the `Dockerfile` must hold the same version: Dependabot opens a second pull request for it (it cannot group two ecosystems), and the image check fails until both are merged.

Use pnpm only, never npm or yarn. Ask in the issue before adding a dependency, and say why it is needed.

A new dependency must pass `pnpm check:licenses`. If it fails because the package has no SPDX licence expression (a NuGet package with only a licence URL, say), check the licence yourself and add an entry under `packages` in `.github/license-policy.json` with the real licence and the reason. A licence that is not on the `allowed` list needs a discussion first: it must be compatible with AGPL-3.0.

Dependabot keeps dependencies current in small grouped pull requests (Mondays, with a 3-day wait after a release). It merges only patch updates of web dev tooling by itself (lint, types, unit-test tooling, commitlint), after their checks pass. Runtime dependencies, NuGet, Playwright, GitHub Actions and the Yjs group are reviewed and merged by hand.

## Before you open a pull request

```bash
pnpm --filter web lint
pnpm --filter web typecheck
pnpm --filter web test
pnpm --filter sidecar lint
pnpm --filter sidecar typecheck
pnpm --filter sidecar test
dotnet format server --verify-no-changes
dotnet test server
pnpm --filter web e2e   # needs Postgres: docker compose -f deploy/docker-compose.dev.yml up -d --wait
```

`pnpm check` runs all of it (about 8 minutes), `pnpm check:push` all but the timing tests and Playwright (about 4), and `pnpm check:fast` the quick part (no server tests, no end-to-end). **Run `pnpm check:push` before every push, and `pnpm check` once before the last push of a pull request**: a pull request's CI only runs the quick checks (lint, types, unit tests, format and build, commit messages), and the server tests and the end-to-end suite run on your machine, then again on main. A red main is fixed or reverted at once.

 Changing the `Dockerfile`, `deploy/` or the image workflow? Build it (`docker build -t inkpact .`) and run the Playwright suite against it: `docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.e2e.yml up -d --wait` with `INKPACT_IMAGE=inkpact`, `POSTGRES_PASSWORD` and `AUTH_SETUP_CODE=E2ESETUP` set, then `E2E_BASE_URL=http://127.0.0.1:8080 pnpm --filter web exec playwright test`. CI does not: it is too slow there.

 Keep a pull request to one testable slice, with its tests and its translations.

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

Pull requests are merged with **Squash and merge** (the only way the repository allows): main gets one commit per pull request, whose message is the pull request's title and description. So the **title** is a conventional commit, and it is what the release notes list; the description holds no `feat:` or `fix:` lines of its own, which would be listed again. The commits on the branch are checked for their format and sign-off before the merge.

The commit types decide the release notes: `feat` (Features), `fix` (Fixes), `perf` (Performance) and `revert` are listed; `docs`, `test`, `ci`, `build`, `chore`, `refactor` and `style` are not. A change that needs care when updating says so in a `BREAKING CHANGE:` footer.

## Releases

A release is one merge. On every push to `main`, release-please keeps a pull request open titled `chore(main): release x.y.z`, with the next version (from the commit types since the last release) and its changelog. Merging it tags the version, makes the GitHub release, and publishes the image as `x.y.z`, `x.y` and `latest`. The image is published only after its checks pass: inspection, backup and restore, the upgrade from the previous release, and Trivy. It is signed, with its SBOM and provenance (`.github/workflows/release.yml` and `image.yml`). Until 1.0 a breaking change raises the minor version.

**Set up once:** release-please needs a GitHub App, because a pull request opened with the workflow's own token runs no checks.

1. Create a GitHub App (Settings → Developer settings → GitHub Apps) with no webhook. Give it repository permissions *Contents*, *Pull requests* and *Issues*, read and write, and let it be installed on any account.
2. Install it on `GotGeeks/Inkpact`, and on `joszz/unraid-community-apps` for the Unraid template's copy (see `docs/self-hosting.md`, "Unraid").
3. In this repository's Actions secrets, set `RELEASE_APP_CLIENT_ID` (the App's client id) and `RELEASE_APP_KEY` (a private key made on the App's page).

Without them, `release.yml` makes no release pull request and says so in its run.
