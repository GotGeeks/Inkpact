# Run B.8: The release pipeline, the Unraid template and the update check

**Model:** S·med in the roadmap; kept. Planned on Opus 5.5 on 2026-11-13. Three PRs, each `/code-review` at medium before it merges: PR 1 adds the server's first outbound call, PR 2 handles signing and a token that can write to the repository.

**Status:** PRs 1 and 2 built (see **Handoff**). The four questions are taken as recommended (the maintainer asked for the card to be built without answering them; they can still be changed before PRs 2 and 3 merge).

## Goal

A release is one merge: merging the release PR tags the version, writes the changelog, and publishes a signed image for amd64 and arm64 with its SBOM and provenance, tagged `x.y.z`, `x.y` and `latest`. Before anything is published, the new image has migrated the previous release's database without losing anything. An Unraid user installs Inkpact from a template and updates it the way they update every other container. The app knows its own version, and tells the administrator when a newer one is out.

## Inputs

- [`docs/architecture.md`](../architecture.md): the B.8 row; "CI/CD" (repository, every pull request, main and releases); "Database migrations" (the upgrade test: "the previous release's database, migrated by the new version, must pass the integration tests"); "Project setup" (`deploy/` holds the Unraid template XML; semantic versioning from v0.1).
- [`docs/runs/1.13.md`](1.13.md) (the image and `edge`; its upgrade check was left to this run), [`1.20.md`](1.20.md) (the security jobs and Trivy), [`B.7.md`](B.7.md) (the `/ready` health check the image already uses; the backup and restore test in `image.yml`).
- [`docs/self-hosting.md`](../self-hosting.md): "Updating", "Unraid", "With a PostgreSQL you already run".
- GarageStack, the model the architecture names ("in the same style as GarageStack"): `.github/workflows/docker-publish.yml` (a native build per architecture pushed by digest, one index with the tags, `cosign sign --recursive` on the index, SBOM and provenance from the build), `documentation/RELEASING.md` ("Unraid Community Apps": the template kept in the app's repository and copied into `joszz/unraid-community-apps`), and its update check (`UpdateCheckService`, `GitHubReleaseClient`: GitHub's latest release once a day, `UPDATE_CHECK_ENABLED=false` turns it off).
- Code:
  - [`.github/workflows/image.yml`](../../.github/workflows/image.yml), [`ci.yml`](../../.github/workflows/ci.yml), [`.github/scripts/check-dco.sh`](../../.github/scripts/check-dco.sh), [`commitlint.config.js`](../../commitlint.config.js);
  - [`Dockerfile`](../../Dockerfile), [`deploy/docker-compose.yml`](../../deploy/docker-compose.yml), [`deploy/test-backup-restore.sh`](../../deploy/test-backup-restore.sh) (the pattern for a script that starts a stack and checks it through the API);
  - [`PermissionService.cs`](../../server/src/Inkpact.Api/Permissions/PermissionService.cs) (`Policies.Admin`, `IsAdmin`), [`EndpointPolicyTests.cs`](../../server/tests/Inkpact.Api.Tests/Permissions/EndpointPolicyTests.cs), [`SecurityHeaders.cs`](../../server/src/Inkpact.Api/Security/SecurityHeaders.cs) (the CSP: the browser never talks to GitHub);
  - [`web/src/settings/SettingsPage.vue`](../../web/src/settings/SettingsPage.vue) (run B.5b's settings screen), [`web/src/layout/UserMenu.vue`](../../web/src/layout/UserMenu.vue);
  - [`web/e2e/jex.ts`](../../web/e2e/jex.ts) (a Joplin export made in code: pages, notebooks, tags, attachments, links).

## What is there now (found while planning)

1. **The app has no version.** No `<Version>` in the projects, no build argument in the `Dockerfile`, nothing in the client. A bug report cannot say which build it is from.
2. **One image, one tag.** Every merge to main publishes `edge` and `sha-<sha>`, both architectures from one QEMU build (up to an hour), unsigned, without an SBOM or provenance. There are no git tags, no GitHub releases and no changelog.
3. **No upgrade test.** 1.13's upgrade check was skipped "while there is only one migration set". There are now 19 migrations, and nothing checks that a database made by an older image is migrated without loss: `MigrationIntegrationTests` only migrates empty databases.
4. **Two things block a release PR made by release-please with the default token.**
   - **Its checks would not run.** A pull request opened with `GITHUB_TOKEN` starts no workflow, so the ruleset's seven required checks never report and the PR cannot be merged without a bypass.
   - **Its commit has no sign-off.** `check-dco.sh` wants a `Signed-off-by` that matches the author on every commit that is not a merge.
   - A tag pushed with `GITHUB_TOKEN` starts no workflow either, so the images cannot be published from a `push: tags` trigger.
5. **Unraid has no template.** The guide sends Unraid users to the Compose Manager plugin and says "a Community Apps template comes with the first release".
   - The image needs a PostgreSQL 18 server, and runs as UID 1654 with a read-only root filesystem.
   - A folder Unraid makes for a template's path belongs to root, so the app cannot write `/data` there.
   - `joszz/unraid-community-apps` (the repository Community Apps already reads for GarageStack) belongs to another account than `GotGeeks/Inkpact`.
6. **The server makes no outbound call.** The update check is its first, and the CSP keeps the browser on `'self'`, so it must go through the server.

## Decisions

1. **The version, from one place.**
   - **Source:** release-please keeps it in `.release-please-manifest.json`.
   - **Stamping:** the image takes it as a build argument (`INKPACT_VERSION`) into `dotnet publish -p:Version=… -p:InformationalVersion=…`. A release build gets `x.y.z`; an `edge` build gets `<manifest version>+edge.<short sha>`, which semver ranks equal to that release, so an `edge` install is not told to update to the release it is built on. A build without the argument says `0.0.0-dev` and never reports an update.
   - **`GET /api/version`** for any signed-in user: `{ "version": "0.1.0", "update": null | { "version", "url" } }`, with `update` only for an administrator. Not anonymous: a version tells an attacker which advisories apply.
2. **The update check: the server asks GitHub, the administrator is told.**
   - **The request:** a hosted service asks `https://api.github.com/repos/GotGeeks/Inkpact/releases/latest` a minute after start and then once a day. It sends `User-Agent: Inkpact/<version>`, nothing about the install, has a ten-second timeout, and re-asks with the last `ETag`. That endpoint skips drafts and pre-releases.
   - **What counts:** the answer is kept in memory only (no table). It counts when its tag parses as a version higher than the app's own and its `html_url` is a release page of `github.com/GotGeeks/Inkpact`; anything else is ignored. A failure is logged with its status code only, and the last good answer stays.
   - **Turning it off:** `UpdateCheck:Enabled` (default on, as the architecture says; `UpdateCheck__Enabled=false` in the compose file, a variable in the Unraid template). It is through `IHttpClientFactory`, part of the shared framework: no new package.
   - **In the client:** the settings screen gets an "About Inkpact" section with the version for everyone. For the administrator it adds "Inkpact x.y.z is out" with a link to its release notes and how to update (Unraid: "Check for updates"; compose: `pull` and `up -d`). The account menu's Settings item carries a marker while an update is out.
   - **No toast:** a notice on every load would nag someone who updates on their own schedule.
3. **release-please makes the releases.**
   - **Config:** `release-please-config.json` and `.release-please-manifest.json` at the root, one package (`.`, release type `simple`).
   - **Versions:** first version `0.1.0`; `bump-minor-pre-major`, so a breaking change before 1.0 raises the minor. Tags are `v0.1.0`.
   - **Changelog:** sections Features, Fixes, Performance and Security; `build(deps)`, `chore`, `docs`, `test`, `ci` and `refactor` are left out.
   - **The first release's history:** `bootstrap-sha` is the commit before this run's last merge, so the first changelog is not three hundred commits long. A short hand-written "What is in 0.1.0" is put on the GitHub release after it is published.
4. **One token for what `GITHUB_TOKEN` cannot do** (question 2).
   - **What it does:** release-please opens and updates its PR with a GitHub App's installation token (`actions/create-github-app-token`), so the PR runs its checks like any other.
   - **Sign-off:** the release commit carries the App's bot identity as its sign-off (release-please's `signoff` option). If that cannot be made to match its author, `check-dco.sh` exempts that one bot by its login, as `ci.yml` does for Dependabot. The commit title (`chore(main): release 0.1.0`) passes commitlint as it is.
   - **Secrets:** the App's client id and its private key are repository secrets: `RELEASE_APP_CLIENT_ID`, `RELEASE_APP_KEY` (PR 2: the token action asks for the client id; the App id is deprecated there).
5. **One publishing path, for `edge` and for releases.**
   - **The build:** `image.yml` gets a `workflow_call` with `version` and `release` inputs. It builds each architecture on its own runner (`ubuntu-24.04-arm` for arm64, free for a public repository) and pushes by digest. Each build has `provenance: mode=max` and `sbom: true`. A merge job ties the two digests into one index and checks that both architectures are in it. It then tags it (`edge` and `sha-<sha>`; or `x.y.z`, `x.y` and `latest`) and signs it keylessly with `cosign sign --recursive` (`id-token: write`).
   - **Before anything is pushed:** the existing checks run on the amd64 image: inspect, backup and restore, the Trivy gate, and the new upgrade test.
   - **`release-please.yml`:** on every push to main it runs release-please. When it reports a release, it calls `image.yml` with the tag's version in the same run, since a tag pushed with a token starts no other workflow.
   - **`edge`** keeps being published on every merge, now signed with an SBOM too: one path, not two.
   - **Trivy rescans** `latest` weekly as it does `edge`.
6. **The upgrade test: an older image's data, migrated by the new one, with nothing lost.**
   - **The script:** `deploy/test-upgrade.sh FROM TO`, in the style of the backup test. It starts the compose stack with `FROM` and creates the administrator. It imports a Joplin export made by `web/e2e/jex.ts` (Node 26 runs it as TypeScript): pages with content, notebooks, tags, attachments, links and a to-do. Then it renames a page (a version and a title change), marks a favorite, and waits for the import and a history capture.
   - **The record before:** the export (`GET` of the full Markdown zip), the page list, the tags, an attachment's bytes, a search answer, the version list.
   - **The swap:** it stops the app, starts `TO` on the same volumes, and waits for `/ready`.
   - **The checks after:** the export's Markdown files and attachments are the same, byte for byte, and so are the page list, the tags and the attachment's bytes. The search finds the same pages, the version list answers, and sign-in works. When `TO` has migrations `FROM` lacks, a dump is in `/data/backups`.
   - **`FROM`** is the newest release (`latest`), or `edge` while there is no release yet; `TO` is the image just built.
   - **When it runs:** in `image.yml` before any push, on main, on a release, on request, and on a pull request that changes `server/src/Inkpact.Data/Migrations/**`. That path filter is added, so a PR that adds a migration is tested before it merges.
   - **What "must pass the integration tests" means here:** the checks above, through the API of the running image. The xUnit suite makes a database per test and cannot run against one migrated database; the architecture's line is reworded to say what is checked, in the same PR.
7. **The Unraid template, against a PostgreSQL container** (question 3).
   - **The file:** `deploy/unraid/inkpact.xml`: `ghcr.io/gotgeeks/inkpact:latest`, port 8080, `/data` on `/mnt/user/appdata/inkpact`, the WebUI, the icon from `web/public/icons/icon-512.png`, category Productivity.
   - **Variables:** the connection string (masked), the setup code (masked), and the optional settings the compose file names (trusted proxy, forward limit, file size, JSON logs, the update check).
   - **Hardening:** `ExtraParams` carries the compose file's: `--read-only --tmpfs /tmp --cap-drop=ALL --security-opt=no-new-privileges:true`.
   - **`<Requires>` says what to do first:** a PostgreSQL 18 container (the official one from Community Apps), with the user and database made once, and `chown -R 1654:1654` on the appdata folder.
   - **The start-up check:** an `/data` that cannot be written makes the app stop at start with one log line that names the command, instead of failing later on the Data Protection keys. It is server code, with a test.
   - Unraid shows the image's own `HEALTHCHECK` (`/ready`, run B.7), so the template adds none.
8. **The template's copy in Community Apps** (question 4).
   - **The sync:** `unraid-sync.yml` copies the template into `joszz/unraid-community-apps` (`templates/inkpact.xml`), with only its `TemplateURL` changed to that copy. It opens a pull request there, with the same App's token (installed on that repository too), whenever `deploy/unraid/inkpact.xml` changes on main. The maintainer merges it, as with GarageStack today.
   - **Switched off for now:** it runs only when the repository variable `UNRAID_CA_SYNC` is `true`, which stays off until the first public release (run 2.11). A template in that repository is listed in Community Apps for everyone. Until then, the maintainer installs from the template's raw URL in this repository.
   - **Template updates:** the template tracks `latest`, so a release needs no change to it, and Unraid's own update check finds a new image.
9. **The compose file's image becomes `latest` once the first release is out** (step 5 of "After the run"). Until then `latest` does not exist and a pull of it fails, so the compose file and the Unraid template run `edge` (review finding 4). `edge` stays documented for a test instance.
10. **No new npm or NuGet package.** New GitHub Actions, pinned by SHA as the others are, and moved by Dependabot's `github-actions` entry:
    - `googleapis/release-please-action`;
    - `sigstore/cosign-installer`;
    - `actions/create-github-app-token`;
    - `actions/download-artifact` (the digests; `upload-artifact` is there already).

## The PRs

| PR | What | Size | Needs the answers |
| --- | --- | --- | --- |
| 1 | The version in the app: the build argument, `GET /api/version`, the update check, the settings screen's "About Inkpact" and the account menu's marker; both languages | Medium | No |
| 2 | The pipeline: `image.yml` per architecture with index, signing, SBOM and provenance; `workflow_call`; `deploy/test-upgrade.sh`; release-please's config and workflow with the App's token; the DCO exemption if needed; the compose default; docs | Large | Questions 1 and 2 |
| 3 | The Unraid template, its consistency test, the start-up check of `/data`, `unraid-sync.yml` (switched off) and the guide's Unraid section | Medium | Questions 3 and 4 |

**After the run** (the maintainer, not a PR):

1. Create the GitHub App, with `contents` and `pull-requests` write. Install it on `GotGeeks/Inkpact` and on `joszz/unraid-community-apps`, and add its two secrets.
2. Merge PR 2. release-please then opens the release PR for 0.1.0; merge it.
3. Check the release:
   - the GitHub release and its changelog;
   - the tags `0.1.0`, `0.1` and `latest` on GHCR;
   - `cosign verify` against the identity the guide gives;
   - the SBOM and provenance (`docker buildx imagetools inspect --format '{{json .SBOM}}'`).
4. Edit the release notes with the "What is in 0.1.0" summary.
5. Switch the compose file and the Unraid template from `edge` to `latest`, in one small PR (`UnraidTemplateTests` wants them on the same tag), and change the guide's "Which image" to match.

## Done when

- **A release is one merge,** with no other step: merging the release PR publishes the tag, the GitHub release with its changelog, and the image as `x.y.z`, `x.y` and `latest`. The image covers amd64 and arm64, is signed, and has its SBOM and provenance.
- **Every merge to main publishes `edge`** the same way, with the version stamped as `<version>+edge.<sha>`.
- **No image is pushed** whose upgrade test, backup and restore test, inspection or Trivy gate failed.
- **The upgrade test fails** when a migration loses data (checked once by hand with a migration that drops a column: see Tests), and passes for the current migrations from `edge`.
- **The release PR runs its checks and can be merged** like any other PR, and its commit passes `commits`.
- **`/api/version`** answers the version to every signed-in user and the update only to the administrator. With `UpdateCheck:Enabled=false`, nothing asks GitHub.
- **The Unraid template** starts the image against a PostgreSQL container, with the hardening flags. An unwritable `/data` stops the app with a message that says what to run.

## Tests

- **PR 1, server:**
  - The version from the assembly (stamped, `edge` and dev).
  - The update check against a fake `HttpMessageHandler`:
    - a newer release, the same, an older one, and a pre-release tag;
    - a tag that does not parse, and an `html_url` that is not one of this repository's release pages;
    - a 304 with the `ETag`, a 403 for the rate limit, a timeout and a 500 (the last good answer stays);
    - switched off (no request at all), and the daily timing with `MutableTimeProvider`.
  - `/api/version`: signed out (401), a member (no `update`), the administrator (with it), and in the endpoint policy test.
  - `LogContentTests`: the check logs no URL or body.
- **PR 1, web:** Vitest for the "About Inkpact" section (member, administrator with and without an update, an error, both languages) and the menu's marker; the settings Playwright spec and the a11y spec with an answered `/api/version`.
- **PR 2:**
  - `deploy/test-upgrade.sh` from `edge` to the built image, locally and in `image.yml`.
  - Once by hand, not committed: a migration that drops a column the export reads makes the test fail. The handoff records that it did.
  - `shellcheck` on the script.
  - The release workflow, tried on a fork or with `workflow_dispatch` and `release: false`: both architectures build, the index is made, nothing is tagged.
  - The first real release (see "After the run") is the end-to-end test; its checks go into the handoff.
- **PR 3:**
  - An xUnit test reads the template with `System.Xml.Linq`. It checks that the XML is well-formed, that every variable is a setting the server reads (against the compose file's list), that the image is `:latest`, that `/data`, port 8080 and the four hardening flags are there, and that the masked fields are the secret ones.
  - The start-up check: a `/data` that cannot be written stops the app with its message. A server test, and a step in `image.yml` that runs the image with a folder owned by another user.
  - `unraid-sync.yml` with `UNRAID_CA_SYNC` off does nothing. With it on (tried once against a branch of the CA repository), it opens a PR whose file differs from the source only in `TemplateURL`.
- **While building:** the test classes you change, `vitest related`, the settings spec. Before each push `pnpm check:push`; before the last push of each PR `pnpm check` once with Postgres up.

## Docs to update

- [`docs/self-hosting.md`](../self-hosting.md):
  - "Updating": `latest` and `edge`, the release notes, what the update notice is, turning it off, and what the check sends;
  - "Unraid": the template, with the PostgreSQL container and the `chown`, and the Compose Manager way kept for a stack;
  - a new "Verifying the image": `cosign verify` with the exact identity, the SBOM and the provenance;
  - "Building the image yourself": `INKPACT_VERSION`.
- [`docs/architecture.md`](../architecture.md):
  - the upgrade test's line under "Database migrations", as decision 6 says;
  - "Main and releases": `edge` signed too, the index, and the Community Apps sync as a PR switched on at 2.11;
  - in the same PR as the change.
- [`.github/SECURITY.md`](../../.github/SECURITY.md): supported versions (the latest release, and `main`) and how to verify an image.
- [`README.md`](../../README.md): the status line ("not released yet" becomes "early releases, 0.x") and a release badge. [`CONTRIBUTING.md`](../../.github/CONTRIBUTING.md): how a release is made (one merge), and that commit types decide the changelog.
- [`docs/test-pass.md`](../test-pass.md): the checks below, under "B.8".
- This card: a handoff per PR.

## Out of scope

- **Release-time checks for run 2.11:**
  - the performance targets against the seeded data set before each release;
  - the docs and screenshots from a demo mode;
  - listing in Community Apps (the sync is built, switched off).
- An all-in-one image with PostgreSQL inside (question 3).
- **Updates applied by the app itself,** or a notice for anyone but the administrator. An administrator page with the version and updates is run 2.9.
- Electron installers and their signing (phase 3).
- Pruning old `edge` and `sha-` images from GHCR.

## Questions

1. **When is the first release cut?**
   - **Recommended:** right after PR 2 merges, as 0.1.0, a normal release. The pipeline is only proven by a real release; the maintainer's Unraid box can then run `latest` with update notices; and the test pass (2.10b) tests what users will install.
   - **Is it public?** The repository is public already, so a release is visible. Nobody is told until 2.11, which also keeps Community Apps off.
   - **The other way:** build everything and cut nothing until 2.11. The release path stays untested until the day it matters.
2. **Which token opens the release PR and the Community Apps PR?**
   - **Recommended:** one GitHub App, owned by `joszz` and installable on both accounts, with two secrets; installation tokens last an hour and the App never expires.
   - **The other way:** fine-grained personal access tokens. One token can only reach one account's repositories, so it takes two, each expiring within a year.
3. **What does the Unraid template run against?**
   - **Recommended:** a PostgreSQL 18 container the user adds from Community Apps. It is the image as it is, tested by every other path, and backups stay `pg_dump`.
   - **The other way:** an all-in-one image with PostgreSQL inside, as GarageStack has. Simpler to install, but a second image to build, scan and test, a database upgrade inside a container, and a backup story of its own: a run of its own if wanted later.
4. **When does Inkpact appear in Community Apps?**
   - **Recommended:** at the first public release (2.11). The sync is built and tried in this run, switched off by a repository variable. Until then the template is installed from its raw URL in this repository.
   - **The other way:** switch it on now, which lists Inkpact for every Unraid user before its documentation and demo are ready.

## For the test pass

- Install Inkpact on Unraid from the template's URL with a PostgreSQL 18 container: the `chown`, the connection string, the setup code. The Docker tab shows it healthy, and it works behind Traefik.
- After the next release, Unraid's "Check for updates" offers it; updating keeps notes, attachments and sign-in, and a dump appears in `/data/backups` when the release has a migration.
- Signed in as the administrator with an older version running, the settings screen says a newer one is out and links to its notes. As another account (from phase 2), it does not.
- With `UpdateCheck__Enabled=false`, the container makes no request to `api.github.com` (the router's or Pi-hole's log).
- `cosign verify` of the installed tag on the laptop, with the guide's command.
- At 2.11: switch `UNRAID_CA_SYNC` on, merge the first sync PR in the CA repository, and find Inkpact in Community Apps.

## Handoff

### PR 1: the version in the app, and the update check

- **The version:**
  - `server/Directory.Build.props` says `0.0.0-dev` (and no commit appended).
  - The `Dockerfile` takes `INKPACT_VERSION` (checked to be made of version characters only) into `-p:Version` (the numbers) and `-p:InformationalVersion` (the whole text).
  - [`AppVersion`](../../server/src/Inkpact.Api/Versions/AppVersion.cs) reads it; [`ReleaseVersion`](../../server/src/Inkpact.Api/Versions/ReleaseVersion.cs) parses and orders versions by semver precedence.
- **The update check:** [`UpdateCheck.cs`](../../server/src/Inkpact.Api/Versions/UpdateCheck.cs).
  - **Options:** `UpdateCheck:Enabled`, `FirstCheckAfter`, `Interval` and `Timeout`, checked at start-up.
  - **The client:** a named `HttpClient` that follows no redirect and has no request log of its own.
  - **What it keeps:** the answer, in memory, once it is checked (newer, not a draft or pre-release, a release page of this repository with no query, fragment, user or port), and the `ETag`.
  - **Answers it refuses:** larger than 1 MiB, with or without a length up front.
  - **The worker:** asks a minute after start, then daily. A development build is inactive: tests and the e2e server never ask.
- **`GET /api/version`**, signed in, with the update for the administrator only ([`VersionEndpoints.cs`](../../server/src/Inkpact.Api/Versions/VersionEndpoints.cs)).
- **The client:**
  - [`stores/version.ts`](../../web/src/stores/version.ts) asks once per signed-in person, and shows an update only when its link is a release page of Inkpact.
  - [`AboutSection.vue`](../../web/src/settings/AboutSection.vue) on the settings screen: the version ("A development build" for `0.0.0-dev`); for the administrator, the release, a link to its notes in a new tab, and how to update on Unraid and with Compose.
  - The account menu: a hint under Settings, for the administrator only, who is the only one who asks.
- **Docs:** the guide's "Updating" (which version runs, the notice, what is sent, turning it off) and "Building the image yourself" (`INKPACT_VERSION`); the compose file's commented `UpdateCheck__Enabled`.

**Where it differs from the card:**

1. **A development build does not ask at all,** rather than asking and never reporting: nothing to report, so no request.
2. **The settings screen has no "loading" line for the version while nobody is signed in** (only in tests): it shows nothing until a request is on its way.

**Tests:**
- **Server:**
  - `ReleaseVersionTests`: parsing, refusals, semver order, edge and development builds.
  - `UpdateCheckTests`:
    - what is sent, and which answers count (versions, drafts, pre-releases, 12 kinds of foreign page);
    - the last good answer kept on 403, 404, 500, bad JSON, a failed connection and a timeout;
    - large answers, the `ETag` and 304, switched off;
    - nothing of the answer or the address in the log;
    - the worker's rhythm, and the options.
  - `VersionEndpointTests`: administrator, member, signed out, with a fake GitHub.
- **Web:** the store, the About section, the menu.
- **Playwright:** `settings.spec.ts` (the development build; an answered update in the menu and on the screen) and `a11y.spec.ts` (both, scanned).

**Review:** `/code-review` (medium) on 2026-11-13, over the three PRs, gave ten findings. The five that are this PR's are fixed in it:
1. **A connection that broke while the answer was read stopped the app.** `HttpIOException` escaped the check and the worker, and a background service that throws stops the host. The check now catches it, and the worker logs any failure of a round it does not expect, then asks again at the next.
2. **Version text with other digits threw.** The pattern used `\d`, which in .NET takes any Unicode digit (`١`), and `int.Parse` then threw. It uses `[0-9]` now.
3. **One person's request was another's.** A request on its way was shared whoever asked, so someone who signed in meanwhile got no answer; it is kept per person now.
4. **The client copied the development-build rule.** It is the server's word now (`development` in `/api/version`), and the rule itself was wrong: an edge build made before the first release is `0.0.0+edge.<sha>`, which counted as a developer's build and would never have heard of 0.1.0. Only an unstamped build (`0.0.0-dev`) is one now.
5. **The client repeated the release-page rule.** It is the server's alone now; the client refuses only a link that is not `https:`, as Vue puts an `href` in as it is.

The other five are fixed in PRs 2 and 3.

### PR 2: the pipeline

- **[`image.yml`](../../.github/workflows/image.yml)**, one path for `edge` and releases:
  - **`version`:** [`.github/scripts/image-version.sh`](../../.github/scripts/image-version.sh) works out what is built: the version (a release's `x.y.z`, or `<manifest>+edge.<sha>`), the tags, whether it is published, and the upgrade test's start (`latest`, or `edge` while there is no release).
  - **`check`:** on amd64 it runs the inspection (now also the stamped version), backup and restore, the new upgrade test, and the Trivy gates.
  - **`build`:** each architecture on its own runner (`ubuntu-24.04-arm` for arm64), pushed by digest, with `provenance: mode=max` and `sbom: true`.
  - **`publish`:** one index of both digests, tagged, checked to hold both architectures, signed with `cosign sign --recursive`, and summed up in the run's summary.
  - **`scan`:** rescans `edge` and `latest` weekly; `latest` is skipped until it exists.
  - **Triggers:** pull requests that change a migration, and `workflow_call` with the release's version. A manual run can build and check both architectures without publishing.
- **[`release.yml`](../../.github/workflows/release.yml):** release-please with the App's token, then `image.yml` for the release in the same run. Without the App's secrets it says so and does nothing, so main stays green until the App is set up.
- **[`release-please-config.json`](../../release-please-config.json)** and `.release-please-manifest.json`: release type `simple`, first version `0.1.0`, `bump-minor-pre-major`, `bootstrap-sha` at the merge of #151, the changelog sections of decision 3 (without "Security": commitlint has no such type, so security fixes are `fix`).
- **[`deploy/test-upgrade.sh`](../../deploy/test-upgrade.sh):** decision 6, with the history capture's round set to two seconds and the update check off in a compose override of its own.
- **`ci.yml`:** the DCO check skips the release pull request: an account ending in `[bot]` (only a GitHub App's can), on a `release-please--` branch.
- **The compose file** stays on `edge` until the first release (review finding 4; see "After the run").
- **Docs:**
  - the guide's "Updating" (`latest`, the version tags, `edge`, images following the release) and "Verifying the image" (`cosign verify`, SBOM, provenance);
  - architecture ("Database migrations", "Main and releases");
  - `SECURITY.md` (supported versions, verifying);
  - README (status, release badge);
  - CONTRIBUTING (the changelog's commit types; "Releases", with the App's setup).

**Where it differs from the card:**

1. **No `signoff` option:** the App's bot identity (`<slug>[bot] <id+slug[bot]@users.noreply.github.com>`) is known only once the App exists, and the check wants the sign-off to match the author exactly. The DCO check skips that one kind of pull request instead (decision 4's fallback).
2. **The secrets are `RELEASE_APP_CLIENT_ID` and `RELEASE_APP_KEY`** (see decision 4).
3. **The image's inspection had a check that could never fail:** `! docker run --read-only …` under `set -e`. It is an `if` now, and actionlint found it.
4. **A release's images follow its GitHub release** by the length of their checks (about half an hour): the update notice can name a release whose image is not there yet. The guide says so. Holding the release as a draft until the images are out would need release-please's draft mode and a step that publishes it; not worth it for 0.x.

**Checked while building:**
- **The upgrade test** from `ghcr.io/gotgeeks/inkpact:sha-657b3c2` (before the history-restore migration) to an image of this branch passed: one migration, after a dump, and every answer the same.
- **A throwaway image** with a migration that deletes every page's tag (built in a separate worktree, never committed) made it fail on `tags.json`.
- **Lint:** actionlint 1.7.7 on every workflow, and shellcheck 0.10 on the scripts, both clean.
- **`image-version.sh`** run locally for a push, a release, and a version that is not x.y.z.

**Not checked:** the release itself and the arm64 build run only on GitHub, after the merge. The first release (see "After the run") is their test; its outcome goes here.

**Review** (the same `/code-review`; this PR's four findings, fixed in it):
1. **The compose file ran `latest`, which does not exist before the first release:** a fresh `docker compose up` failed. It runs `edge` until then ("After the run", step 5).
2. **A manual run on any branch published `edge`,** with a signature the guide's `cosign verify` refuses. `image-version.sh` publishes only from `refs/heads/main`; elsewhere it builds and checks.
3. **A rescan or a manual run could cancel a merge's `edge` waiting to publish:** a concurrency group holds one waiting run, and they shared it. Each now has a group of its own.
4. **The DCO check skipped the whole release pull request,** so a person's commit pushed onto it was never checked. The check runs there too, and skips only the release bot's own commits, known by name and noreply address both.

`scripts.test.mjs` now runs both scripts: `check-dco.sh` in throwaway repositories (signed, unsigned, the bot, someone else on the release branch, look-alikes of the bot), and `image-version.sh` with a fake `docker` (a merge, a release, a bad version, manual runs on main and elsewhere, a pull request).
