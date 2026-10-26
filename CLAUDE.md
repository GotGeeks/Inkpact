# Inkpact

Self-hosted, multi-user notes app with real-time collaborative editing. Replaces Joplin for the maintainer first, then for other self-hosters. AGPL-3.0, no paid features.

- Architecture and decisions: `docs/architecture.md` (frozen v1; change it only when implementation proves it wrong, in the same PR)
- Designs: `docs/design/` (screenshots + `DESIGN.md` with the design tokens)
- Current phase: **Phase 0 (sync spike)**, then **Phase 1A (Joplin escape velocity)**. Do not build features from later phases unless the issue says so.

## Repository layout

```
server/    ASP.NET Core (.NET 10): REST API, WebSocket sync hub, compaction worker
  sidecar/ Node process running Yjs, the only decoder of Yjs updates (pnpm package, TypeScript run by Node 24, no build)
web/       Vue 3 + TypeScript client (Vite, Pinia, TipTap, Tailwind v4, Reka UI)
desktop/   Electron shell around web/ (phase 3, not yet)
deploy/    docker-compose.yml, Unraid template
docs/      architecture, design, self-hosting guide
```

## Commands

```bash
# Client (pnpm only, never npm or yarn)
pnpm install --frozen-lockfile
pnpm --filter web dev
pnpm --filter web test          # Vitest
pnpm --filter web lint          # ESLint incl. vue-i18n rules + Prettier
pnpm --filter web typecheck     # vue-tsc
pnpm --filter web e2e:install   # once: Playwright browsers
pnpm --filter web e2e           # Playwright against the built bundle and the real server (needs Postgres)

# Yjs sidecar (server/sidecar)
pnpm --filter sidecar test      # Vitest
pnpm --filter sidecar typecheck # tsc
pnpm --filter sidecar lint      # Prettier

# Server
pnpm install --frozen-lockfile  # before dotnet test: the tests start the real sidecar, which needs Node 24 and its dependencies
dotnet build server
dotnet test server              # unit + Testcontainers integration (needs Docker)
dotnet format server --verify-no-changes
dotnet ef migrations add <Name> --project server/src/Inkpact.Data

# Postgres for development and e2e (port 5432)
docker compose -f deploy/docker-compose.dev.yml up -d --wait

# Everything
docker compose -f deploy/docker-compose.yml up
```

Run the relevant tests, lint and typecheck before saying a task is done.

## Non-negotiables

These hold for every change. Never weaken one to make something pass; stop and ask instead.

1. **Tests with every change.** New logic gets unit tests in the same PR. Critical paths need thorough tests: sync, compaction, tree operations, permissions, Joplin import, sanitising. Compaction and tree operations also get property-based tests (FsCheck). Never delete or skip a failing test to get green.
2. **No hard-coded user-facing strings.** Everything goes through vue-i18n with keys in both `en.json` and `nl.json`. English is the fallback. The server returns error codes, the client translates them. Format dates, numbers and sizes with `Intl`.
3. **Strict CSP stays strict.** No inline scripts, no inline `style` attributes in templates, no `eval`, no `v-html` with unsanitised content, no external hosts. Use the Vue runtime-only build. A Playwright test fails on any CSP violation.
4. **Styling is Tailwind only**, using the theme tokens from `DESIGN.md` (colors, type scale, spacing, radii). No arbitrary hex values or one-off CSS. Mobile-first.
5. **Security baseline from `docs/architecture.md` → "Security from day one".** In short: deny-by-default authorisation on every endpoint and WebSocket connection, sanitise all imported or pasted HTML, serve attachments safely, cookie auth with the Postgres-backed ticket store, Argon2id passwords.
6. **pnpm only.** Ask before adding any dependency (npm or NuGet) and say why it is needed. Never add packages to `onlyBuiltDependencies` without asking.
7. **Conventional commits with DCO sign-off**: `git commit -s -m "feat(editor): ..."`.

## Backend conventions

- .NET 10, minimal APIs, nullable reference types on, warnings as errors.
- EF Core with Npgsql. Migrations are the only way to change the schema. Raw SQL only for recursive tree queries.
- Every endpoint declares an authorisation policy; the single permission service resolves roles (rules in architecture → "Permission resolution").
- Tests: xUnit, NSubstitute, Shouldly (not FluentAssertions), Testcontainers with real Postgres. No in-memory database provider.
- The sync hub treats Yjs updates as opaque blobs. Decode them only for compaction, diffs and text extraction, in the Yjs Node sidecar (YDotNet was rejected in the spike, see `docs/spike-findings.md`).
- One multiplexed WebSocket per client session; every message carries its page id.

## Frontend conventions

- `<script setup lang="ts">` with the Composition API. Pinia for shared state.
- Components from Reka UI (headless, accessible), styled with Tailwind. Keyboard navigation and labels on everything interactive.
- Editor: TipTap with the Yjs collaboration extension, Notion-style block handles and a slash menu. Lazy-load the editor route.
- Tests: Vitest + Vue Test Utils for components, stores and composables; Playwright for end-to-end on desktop and a phone viewport.

## Data and sync rules (easy to get wrong)

- **Page titles are metadata** on the `pages` row, not the first heading of the document.
- **Tree operations go through the REST API**, never through the CRDT. Follow the conflict table in architecture → "Tree and metadata conflicts".
- **Sibling order uses fractional index keys** from a maintained library; never renumber siblings.
- **Compaction must meet every guarantee** in architecture → "Compaction guarantees": transactional, watermark-based, advisory lock per page, idempotent, verified against a full replay. In phase 1A merged updates are archived, not deleted.
- **Attachments** are stored by SHA-256 behind the storage interface and referenced by id, never by URL.
- **Joplin import** follows the mapping table and the three-way re-import rules; it must be repeatable and lose nothing.

## Using the designs

The Stitch designs in `docs/design/` are a **visual reference, not code**. Their HTML loads Tailwind from a CDN and uses inline styles, which break the CSP and the pnpm setup: never copy it. Rebuild components in Vue using the tokens from `DESIGN.md`.

Ignore these design elements; they are not in the plan:
- Knowledge graph
- Markdown source pane next to the editor
- "CRDT vault buffer" widget
- Claims about cryptographically signed updates

Version history, to-dos, presence avatars and sharing appear in the designs but belong to phase 1B or 2.

## How to work

- One GitHub issue per session. Read the issue and the relevant section of `docs/architecture.md`, then propose a short plan before writing code.
- Keep PRs small and vertical: one testable slice, with its tests and i18n strings.
- If the plan turns out to be wrong or ambiguous, say so and propose an update to `docs/architecture.md` instead of silently deviating.
- Don't refactor unrelated code in the same PR.
- Phase 0 code is throwaway: keep it simple, but its sync scenarios become the first integration tests in phase 1A.