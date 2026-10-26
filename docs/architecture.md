# Inkpact — project plan

Oct 22, 2026 · @Jos · v1.1, Oct 24, 2026: roadmap split into runs, model and effort per run added

Frozen as v1 on 22 October 2026: this plan changes only when implementation uncovers a problem. v1.1 changes only how the work is executed (runs, models, effort), not what is built.

## Vision and goals

Inkpact (ink + pact: an agreement written in ink, the promise of conflict-free sync) is a fully free, self-hosted notes app with real multi-user collaboration, Postgres storage and instant sync — no seat limits, no open-core paywall. The web UI ships first and is the only interface until it is solid; a desktop app follows on the same codebase.

**The gap:** Joplin has weak mobile/web and a bloating sync store; Trilium is single-user; AFFiNE and AppFlowy cap self-hosted users; Docmost and Outline lack offline and app clients.

**Goals for v1**

- Responsive web UI that works well on a phone browser (and installs as a PWA)
- Multiple users, private and shared workspaces, read/write permissions
- Real-time collaborative editing with automatic conflict resolution (CRDT)
- Postgres as the single source of truth; storage that stays compact over time
- One `docker compose up` deployment, easy to run on Unraid
- Import from Joplin and Markdown, export to Markdown at any time
- Easy sharing of a notebook or a single note with a user or by link (after the MVP, in phase 2)

**Core principle:** your data is yours. At any time you can export everything to standard Markdown plus attachments, without needing Inkpact to read it.

**No paid features:** every feature is free and open source under AGPL-3.0, for everyone. Development is funded by feature bounties, not by paywalls or seat limits.

**Non-goals for v1:** databases/tables-as-apps, whiteboards, AI features, plugins, end-to-end encryption, native mobile apps.

## Architecture and tech stack

One ASP.NET Core container serves the web app, a REST API and a WebSocket sync hub, backed by Postgres; the desktop app later wraps the same web bundle in Electron.

All edits flow through the sync hub as Yjs updates; everything else (tree, permissions, search, files) goes through the REST API.

Inkpact runs as exactly one application instance. The sync hub keeps connection state in memory, so running several instances or clustering is out of scope, and the README says so.

| Layer | Choice | Why |
| --- | --- | --- |
| Backend | ASP.NET Core (.NET 10), minimal APIs + WebSockets, EF Core | Your strongest stack; fast, single container |
| Database | PostgreSQL via Npgsql | Requirement; `bytea` for CRDT state, built-in full-text search |
| CRDT | Yjs on the client, Yjs in a Node sidecar on the server where needed | Most mature CRDT ecosystem, editor bindings ready-made |
| Editor | TipTap (ProseMirror) with the Yjs collaboration extension, Notion-style block handles and a slash menu | Proven rich editor, works in mobile browsers |
| Frontend | Vue 3 (Composition API), TypeScript, Vite, Pinia, pnpm workspaces | Official TipTap bindings (@tiptap/vue-3); same bundle for web and desktop |
| Styling | Tailwind CSS v4 with headless components (Reka UI) | Utility-first, easy responsive layouts and dark mode, no component-library lock-in |
| i18n | vue-i18n on the client, IStringLocalizer on the server | English and Dutch from the first commit |
| Testing | xUnit, NSubstitute, Testcontainers, Vitest, Vue Test Utils, Playwright | Unit tests for everything, plus integration and end-to-end |
| Desktop | Electron (phase 3) | Reuses the web bundle |
| Deployment | Docker image + compose file, Unraid template | Fits your Traefik-fronted Unraid setup |

## Data model and sync design

Each page is one Yjs document; the server stores its update log in Postgres and compacts it into a snapshot, so storage stays proportional to current content plus bounded history — the fix for Joplin's ever-growing store.

**Page titles are metadata,** deliberately separate from the document content: the editor shows the title as its own field, not as a first heading. That keeps tree queries, search and renames fast and gives each title one source of truth.

**Core tables (Postgres)**

| Table | Holds | Notes |
| --- | --- | --- |
| `users` | Accounts, password hash, OIDC subject | Optional OIDC later (Authentik, Authelia) |
| `workspaces` | Name, owner | Every user gets a private workspace on signup |
| `workspace_members` | User, workspace, role | Roles: owner, editor, viewer |
| `pages` | Id, workspace, parent page, title, sort key (fractional index), timestamps, soft-delete flag, to-do flag, completed at, due at, external source type and id (Joplin now, other importers later) | Tree lives here, not in the CRDT, so permission checks stay cheap |
| `page_snapshots` | Page id, compacted Yjs state (`bytea`), state vector, watermark (highest update sequence number folded in), version, update count, whether the page's full history is still in the archive | One current row per page |
| `page_updates` | Page id, sequence, Yjs update (`bytea`), user, timestamp | Appended live; folded into the snapshot by a compaction job |
| `page_updates_archive` | The same columns as `page_updates`, with the sequence number the row had, and when it was archived | Where merged updates go in phase 1A; purged after 30 days |
| `page_versions` | Page id, snapshot, created at, created by | Full snapshots, chosen for simplicity over storage; thinned by a configurable retention: everything for 24 hours, then daily for 30 days, then weekly for a year; history can be capped or turned off per workspace if storage becomes a problem |
| `attachments` | Id, page, file name, MIME type, size, storage key | Files on disk or S3-compatible storage, not in Postgres |
| `pages (search)` | Columns on the page row, no separate table: plain-text extract, `tsvector` | Postgres full-text search: titles weighted above tags above content, the simple configuration with unaccent so English and Dutch notes both match, prefix matching for search-as-you-type. Other backends (such as vector search for related notes) can be added later without changing page storage |

**Sharing (phase 2):** a `shares` table links a page to a user or a link token, with a role (viewer, editor), the user who created it, optional expiry and optional password. A notebook is a top-level page, so sharing it covers its whole subtree; access follows the permission resolution rules below.

**To-dos and reminders (phase 2):** any page can be marked as a to-do with a completed state and an optional due date, stored as columns on `pages`. A `reminders` table holds page, user, remind-at time and delivery state; a background job sends due reminders as web push (PWA), email, or later desktop notifications. The phase 1 Joplin import already stores to-do status and due dates, so nothing is lost before the feature exists.

**Sync flow**

1. The client opens one WebSocket per session and subscribes to each open page over it; every message carries its page id, and on subscribe the client sends that page's state vector.
2. The server checks membership, loads the page's stored updates, and asks the Yjs sidecar for the diff the client is missing. It replies with that one diff and the server's own state vector, which the client needs to send the server what it lacks. When the sidecar cannot answer (not started yet, restarting, no Node, or an update it cannot decode) it replies with the stored updates in order and an empty state vector, and the client works out what the server lacks, so syncing never depends on the sidecar. The reply is `[type][pageId][state vector][count][updates]`. After every (re)connect the client subscribes again with its own state vector, applies the reply and sends back what the server lacks (in log mode it works the server's state vector out from the stored updates), so a lost frame never matters: every sync repairs everything. The client pings every 3 s and drops a connection that has been silent for 8 s, because a phone that loses its network does not close its socket.
3. Each local edit is sent as a small Yjs update; the server appends it to `page_updates` and broadcasts it to other connected clients.
4. Awareness messages (cursors, who is viewing) are relayed but never stored.
5. A background job merges updates into the snapshot once a threshold is hit (500 updates, or 10 minutes without a new one) and moves the merged rows to the archive table in the same transaction; in phase 1A they are kept for 30 days and then purged. Writing a version point if due waits for phase B (run B.5).
6. In the same transaction the page's plain-text extract (`content_text`) is refreshed for search. The title belongs to the tree and is not touched.

**Offline (desktop phase):** the client keeps its Yjs state in IndexedDB (web) or SQLite (desktop) and replays on reconnect; CRDT merge makes this conflict-free by design.

**Server CRDT handling:** keep the server mostly agnostic — store and relay opaque update blobs, and only decode them for compaction, diffs and text extraction. A small Node sidecar running Yjs does that work; YDotNet was evaluated in spike 0.4 and diverges from Yjs on out-of-order updates (see `docs/spike-findings.md`). Compaction applies updates one at a time, never in one transaction, and verifies by comparing Yjs snapshot fingerprints. The sidecar is a child process that the server supervises: it starts in the background (the app never waits for it), is restarted with a backoff when it dies or hangs, and speaks length-prefixed binary frames over stdin and stdout. It has no network port and starts with an empty environment (the server's holds the connection string), a V8 heap limit and Node's permission model, so it can read only its own folder and cannot write files or start processes. Appending an update never needs it.

## Working in runs with Claude Code

Inkpact is built with Claude Code on a Claude Pro subscription. Pro usage is limited per 5-hour window and per week, so the whole roadmap is cut into **runs**: one run is one focused Claude Code session that fits in a single 5-hour window and ends in a mergeable pull request.

### What a run is

- **One deliverable, one PR.** A run touches one feature slice, ideally under about 10–15 files, and has a done-criterion you can check yourself.
- **Fresh context.** Start every run with `/clear` (or a new session). The run gets its context from `CLAUDE.md`, this document and the run card, not from earlier chat history.
- **Starts from a run card.** Each run has a short card in `docs/runs/<id>.md`: goal, inputs (which sections of this plan apply), done-criterion, and model and effort. Write the card before the run, or let the planning step write it.
- **Ends green and handed off.** A run ends with tests passing, a commit or PR, and a three-line handoff at the bottom of its card: what was done, what was left, anything surprising. The next run reads only that.
- **Stops cleanly when the budget runs out.** If the window is close to its limit, stop at the last green commit, write the handoff and continue in the next window as `<id>b`. Never leave a run half-merged.
- **Tests, i18n, accessibility and security are part of every run**, as in the quality standards, not separate runs.

### Model and effort

Only two models are used: **Sonnet 5.5** for nearly all implementation, **Opus 5.5** for the parts where a wrong design loses data or opens a security hole. Fable is not used. Opus uses a Pro window several times faster than Sonnet, so it is reserved for planning and reviewing critical runs, not for typing out code.

| Notation | Meaning | Used for |
| --- | --- | --- |
| **S·low** | Sonnet, low effort | Mechanical work: config, docs, translations, renames, CI YAML from an existing example |
| **S·med** | Sonnet, medium effort | The default: UI components, CRUD endpoints, wiring, tests for clear specs |
| **S·high** | Sonnet, high effort | Implementation with tricky logic but a settled design: import parsing, search queries, attachment handling |
| **O·high → S** | Opus high in plan mode, then Sonnet executes | Critical design: sync, compaction, tree conflict rules, auth and security, permissions, re-import merge. Opus writes the plan into the run card; Sonnet (medium or high, as stated) implements it. Use the `opusplan` model setting if your Claude Code version has it, otherwise switch with `/model` after the plan is approved |
| **O·high** | Opus high for the whole run | Only where the work is investigation rather than typing: the spike's unknowns, and debugging data-loss bugs |
| **O·xhigh** | Opus extra-high effort | Escalation only: a property test that keeps failing, a merge that loses data, a bug two Sonnet runs could not fix. Never planned in advance |

Rules that keep the budget healthy:

- **Max effort is never planned.** Use it only for a bug that blocks the gate, and only after xhigh failed.
- **Fallback when Opus is not available or the weekly cap is near:** run the O-step on Sonnet high and add a short review run on Opus later (`/code-review` at high) before the PR merges. Critical PRs (sync, compaction, auth, permissions, import, sanitising) never merge without one Opus pass, plan or review.
- **No subagents by default.** Each subagent starts cold and re-reads context; on Pro that is the expensive path. Do the work in the main session.
- **Keep logs out of the context.** Redirect `dotnet build`, `dotnet test` and Playwright output to a file and summarise it with the local model (`summarize_file`) instead of reading it.
- **Plan mode for anything over an hour of work,** even on Sonnet: a reviewed plan prevents the most expensive failure, a run that builds the wrong thing.
- **Code review:** `/code-review` at medium on Sonnet for ordinary runs; at high on Opus for critical runs.
- **Rhythm:** about one run per window, at most two windows a day. Expect two to three Opus-heavy runs per week at most; weeks with more critical runs simply go slower.

### Run budget

About 60 runs to the first public release, of which about 18 need Opus for planning or the whole run. At four to six runs a week that is roughly three to four months to phase 2's gate, with phase 1A (the point where Joplin can be retired) after about 25 runs.

## Phased roadmap

Four phases, each closed by a gate you can test yourself; the web UI is the only interface until phase 3, and mobile apps are explicitly out of scope until after that. Each phase is a list of runs in the order they should be done; each run lists what it delivers and its model and effort.

Phase 1A, Joplin escape velocity, is the main milestone: the point where you can stop using Joplin.

### Phase 0 — Sync spike (throwaway code)

Prove the sync core before building anything around it. Spike code lived in `spike/` and was deleted after the gate (it stays reachable at the git tag `phase0-spike`); only the findings and test scenarios survive.

| Run | Delivers | Model |
| --- | --- | --- |
| 0.1 | ASP.NET Core WebSocket endpoint that relays Yjs updates between clients of one page; minimal Vite page with TipTap and the Yjs collaboration extension; Postgres in a compose file | S·med |
| 0.2 | Append updates to Postgres and rebuild the page state on reconnect; one WebSocket per session with page-id multiplexing; several tabs of the same user on one page — no duplicate updates, presence shown sensibly | O·high → S·med |
| 0.3 | Scripted test: two browsers editing at once, one going offline and reconnecting, server restart mid-edit (Playwright, reused in 1A) | S·med |
| 0.4 | Try YDotNet for decoding a snapshot to plain text and merging updates; note what works; if it falls short, a minimal Node sidecar prototype | O·high |
| 0.5 | Compaction stress test: 50,000 random edits with repeated compaction; the plain text and Yjs state must match an uncompacted replay every time | O·high → S·high |
| 0.6 | TipTap on Android: Chrome and Firefox, with Gboard and the Samsung keyboard if available — typing, autocorrect, selection, lists, the on-screen keyboard. You test by hand from a checklist Claude writes; Claude fixes what is fixable | S·low |
| 0.7 | `docs/spike-findings.md`: what worked, YDotNet or sidecar decision, numbers from the stress test, Android issues; sync scenarios listed for 1A | S·low |

**Gate:** concurrent and offline edits always merge with nothing lost, and storage does not grow without bound after compaction.

### Phase 1A — Joplin escape velocity

The smallest version you can use daily instead of Joplin. Build login, tree, editor, sync and Joplin import first, so you can start trialling within weeks; search, attachments and export follow before Joplin is retired. Tests, i18n, accessibility and the security baseline are not separate tasks but part of how each item is built: set up at the start they cost hours, retrofitted they cost weeks.

**Foundation**

| Run | Delivers | Model |
| --- | --- | --- |
| 1.01 | Monorepo scaffold: `server/`, `web/`, pnpm workspaces with Corepack and the supply-chain settings, Vue 3 + Vite + Pinia, Tailwind v4 theme tokens, Reka UI, vue-i18n with English and Dutch and its ESLint plugin, xUnit + NSubstitute + Shouldly, Vitest; repository `CLAUDE.md` with commands, conventions and the run rules above | S·med |
| 1.02 | CI for pull requests: dotnet format, build, unit tests, ESLint, Prettier, vue-tsc, Vitest; commitlint, DCO check, issue forms, CONTRIBUTING, SECURITY.md, CODEOWNERS | S·low |
| 1.03 | EF Core with Npgsql, first migration (users, workspaces, pages), migrations on startup with `pg_dump` beforehand, refusal to start against a newer database; Testcontainers integration test base | S·high |
| 1.04 | Security headers and strict CSP, SPA served by ASP.NET Core, Playwright harness on desktop and phone viewports that fails on any CSP violation | O·high → S·med |
| 1.05 | Local account and login: Argon2id, cookie authentication with the Postgres `ITicketStore`, `__Host-` cookie, logout everywhere, Data Protection keys on the data volume, login page in both languages | O·high → S·high |
| 1.06 | CSRF tokens, login rate limiting with lockout backoff, security audit log table, deny-by-default permission service with the test that fails for any endpoint without a policy | O·high → S·high |

**Core of the app — trial starts after 1.14**

| Run | Delivers | Model |
| --- | --- | --- |
| 1.07 | Tree API: create, rename, move, nest, delete to trash and restore, following the conflict rules; fractional indexing; page version numbers; FsCheck property tests for move and delete sequences | O·high → S·high |
| 1.08 | Tree UI: sidebar with nesting, drag and drop, rename inline, trash view, keyboard navigation, mobile drawer | S·med |
| 1.09 | Production sync hub from the spike: cookie auth, Origin check, message size and rate limits, permission check on subscribe, persistence and broadcast; spike scenarios become integration tests | O·high → S·high |
| 1.10a | The Yjs sidecar: a supervised Node child process with binary framing and the codec contract tests; the sync hub answers sync step 1 with a diff and the server's state vector, and falls back to the stored log when the sidecar is down | O·high → S·high |
| 1.10b | Compaction worker with every compaction guarantee: transaction, watermark, advisory lock, idempotence, verification, archive table and its 30-day purge; plain-text extract for search; property tests with injected crashes | O·high → S·high |
| 1.11a | The TypeScript sync client: one socket per tab, ping, reconnect with backoff, resync in diff and log mode, close codes, a status pill on the page; tested against a fake hub and in the browser | O·high → S·high |
| 1.11b | Editor: title field, headings, lists, checklists, code blocks, tables, links, on the sync client; the first paragraph made once; the browser sync scenarios from the spike as Playwright tests; DOMPurify on paste | S·med |
| 1.12 | Editor polish: Notion-style block handles, slash menu, editor lazy-loaded, mobile fixes from the spike findings | S·med |
| 1.13 | Docker image (non-root, read-only root filesystem), compose file, `edge` image to GHCR on merge; running on Unraid behind Traefik on a test subdomain | S·med |
| 1.14 | Attachments, as far as the import needs them: storage interface with local volume, SHA-256 dedupe, upload, download, inline images, safe serving headers, size limits | S·high |
| 1.15 | Joplin import part 1: JEX reading, notebooks, notes, tags, resources, timestamps, to-do and alarm fields; Markdown to editor content with server-side sanitising; import report | O·high → S·high |
| 1.16 | Joplin import part 2: repeatable re-import with the three-way comparison and conflict copies, internal `:/noteId` links, large-note handling; 5,000-note timing check | O·high → S·high |

**Trial starts:** import your Joplin notes and use Inkpact next to Joplin.

**Before Joplin is retired**

| Run | Delivers | Model |
| --- | --- | --- |
| 1.17 | Full-text search: `tsvector` with weights, unaccent, prefix matching; search UI with search-as-you-type | S·high |
| 1.18 | Export of everything to Markdown plus attachments as a zip, readable without Inkpact; round-trip test with the import | S·med |
| 1.19 | Responsive and accessibility pass: phone layout of every screen, keyboard navigation, labels, contrast; Playwright checks on the phone viewport | S·med |
| 1.20 | CI security: CodeQL, dependency review, `pnpm audit`, vulnerable NuGet check, Trivy image scan, licence check, Renovate configuration | S·low |
| 1.21+ | Bug-fix runs from the trial, one bug or small group per run | S·med; O·high for anything that loses or corrupts data |

**Gate:** you use it daily on phone and laptop next to Joplin for two weeks, re-importing as needed, and no note is lost or broken. Then Joplin is retired.

### Phase 1B — Daily-use polish

| Run | Delivers | Model |
| --- | --- | --- |
| B.1 | Links between notes with a backlinks panel; a link to a deleted page stays in place and is marked as broken | S·high |
| B.2 | Tags in the UI (already stored by the import), with add/remove set operations | S·med |
| B.3 | Favorites and recently opened notes; light and dark theme following the system setting | S·low |
| B.4 | Quick switcher and command palette (Ctrl+K), keyboard shortcuts | S·med |
| B.5 | Version history with restore and a retention setting; retention job | O·high → S·high |
| B.6 | PWA: installable, with an offline cache and IndexedDB Yjs state | O·high → S·med |
| B.7 | Health endpoint, structured logging, documented backup and restore (pg\_dump plus the attachments volume), admin password reset via CLI | S·low |
| B.8 | Release pipeline: release-please, versioned and signed images with SBOM and provenance, Unraid template and its automatic update, update check, migration upgrade test in CI | S·med |

**Gate:** nothing in daily use makes you miss Joplin.

### Phase 2 — Multi-user and sharing

| Run | Delivers | Model |
| --- | --- | --- |
| 2.1 | Invite users; shared workspaces with owner, editor and viewer roles | S·high |
| 2.2 | Permission resolution as designed: one function, one recursive query, per-request cache, table-driven test of every combination | O·high → S·high |
| 2.3 | User shares: share dialog on any notebook or note in two clicks, "Shared with me" section in the sidebar, share entries in the audit log | S·high |
| 2.4 | Share links for people without an account: view-only or edit, optional expiry and password, revocable | O·high → S·high |
| 2.5 | To-do notes and the to-do overview: overdue, today, upcoming, completed, filterable per notebook | S·med |
| 2.6 | Reminders at a chosen time, delivered as web push (PWA) and optionally by email | S·high |
| 2.7 | Live cursors and presence via Yjs awareness; per-user history: who changed what, and when | S·med |
| 2.8 | Optional OIDC login (Authentik, Authelia, Keycloak) | O·high → S·med |
| 2.9 | Admin page: users, storage use, backups; per-user quotas and storage reports | S·med |
| 2.10 | Before the first release: metrics endpoint, password reset by email, threat model document | O·high (threat model), S·med (rest) |
| 2.11 | First public release (v0.x): self-hosting docs, demo mode with screenshots, performance targets checked against the seeded data set | S·med |

**Gate:** a second person in your household uses a shared workspace without help.

### After the first public release — Feature bounties

Bounties are Inkpact's only funding model. Supporters buy credits and pledge them to the features they want, as in nzb360. The features with the most credits show what to build next and fund the work. Bounties start once real users exist, small and on existing tools: a custom payment and credits platform is a product of its own and only gets built if the simple version proves people want it. Bounties express interest and fund development effort; they never guarantee a feature or a delivery date.

- [ ] Step 1: GitHub issues labelled `bounty`, funded through an existing funding platform, with no custom payment or credits code (one S·low run for labels, templates and the rules page)
- [ ] Published rules: bounties are not delivery guarantees; what happens to funds when a feature ships, is dropped, or is merged with another
- [ ] Check KvK registration and tax obligations for the income before taking the first payment (your task, not a run)
- [ ] Step 2, only if step 1 gets real use: in-app "Feature bounties" page, fed through the server so the CSP stays strict, with an opt-out setting like the update check (S·med)
- [ ] Step 3, only if needed: own credits system through a merchant-of-record provider that handles EU VAT (planned separately, O·high → S·high)

### Phase 3 — Desktop app

| Run | Delivers | Model |
| --- | --- | --- |
| 3.1 | Electron shell loading the same web bundle, pointed at a configurable server | S·med |
| 3.2 | Local SQLite store for Yjs state, full offline editing, sync on reconnect; offline tree changes queued and replayed through the conflict rules | O·high → S·high |
| 3.3 | System tray, global quick-capture shortcut, native reminder notifications | S·med |
| 3.4 | Signed builds for Windows, macOS and Linux with auto-update via CI | S·med |

### Later (not planned yet)

Android app via Capacitor around the web bundle, web clipper, end-to-end encryption, public page sharing.

## Design rules

These rules are fixed before the code that depends on them is written, and each one gets its own tests.

### Tree and metadata conflicts

Yjs merges note content, but the page tree, titles, tags and to-do fields live in Postgres and need their own rules. All tree operations go through the server, which applies them one at a time in a transaction and validates each against the current state.

| Situation | Rule |
| --- | --- |
| Rename vs rename | Last write wins; both titles stay visible in the page's history |
| Move vs move | Last write wins, but a move into the page's own subtree (a cycle) is rejected |
| Move into a deleted parent | Rejected; the client refreshes its tree |
| Edit vs delete | Content edits to a page in the trash are kept; the page stays in the trash |
| Delete a parent | Its children go to the trash with it and come back with it |
| Restore when the parent is gone | Restored to the workspace root, with a notice |
| Restore vs move | Restored to its last location; if that is no longer valid, to the root |
| Tag changes | Stored as add and remove operations on a set, so concurrent changes merge instead of overwriting |
| To-do completed vs edited | Last write wins per field; completing a to-do never touches its content |
| Offline tree changes (desktop, phase 3) | Queued and replayed through these same rules; rejected changes are shown to the user |

Every page carries a version number, raised by every change to its title or place. Clients send the version they last saw, so the server can detect and log conflicts even where last write wins: the change is applied, logged as `tree_conflict`, and the answer says `conflict: true`.

**How the rules are kept:**

- **One change at a time per workspace.** Every tree operation runs in its own transaction and starts by raising the workspace's `tree_version`, which locks the workspace row until the transaction ends. Each operation is therefore checked against the state the one before it left, which is what stops two moves that are each valid from forming a cycle together, and two inserts at the same spot from getting the same key. A unique index on the keys of live siblings (the top level included) makes a tie a database error too.
- **The trash marks only the page that was deleted.** Its descendants are in the trash because an ancestor is, so they leave and come back with it, and a child that was trashed on its own earlier stays in the trash when its parent is restored. The trash shows the top-most trashed pages, which is what a person restores. A page that is only hidden by an ancestor cannot be restored alone (`restore_the_parent`). Metadata operations on a page in the trash are refused (`page_in_trash`).
- **Restoring** puts a page back between the same neighbours when its parent is still usable and its old key is free; if the key was taken it goes right after the nearest key below it; if the parent is gone or in the trash it goes to the end of the top level and the answer says `restoredTo: "root"`.
- **Clients never send a key.** They say where a page goes by naming its neighbours (`afterId`, `beforeId`, which must be live siblings next to each other), and only the server makes the key.

**Ordering:** siblings are sorted by fractional index keys, strings that always leave room between two neighbours, using the established fractional-indexing algorithm (as used by Figma and Linear) rather than a home-grown scheme; use a maintained library on both sides, and only if no maintained .NET one exists, port the small reference implementation and test it against the original. Dragging a page writes one row and never renumbers its siblings.

**The port:** no maintained .NET library exists (the only package is a 2024 release with no license). `FractionalIndex` is therefore a port of `fractional-indexing` 4.0.0 (CC0) with its default alphabets, and a test checks it against about 2,100 cases made by the original package (the generator script is in the repository; the original is not a dependency), plus FsCheck properties on ordering and key length.

### Permission resolution

A user's role on a page is the highest role granted by any of these, and there are no deny rules:

1. Workspace membership (owner, editor, viewer)
2. A user share on the page itself
3. A user share on any ancestor page, so sharing a notebook covers its whole subtree

What follows from this:

- Grants only add up, so no grant can take away access another one gives; checks stay predictable and cheap
- Permissions follow location: moving a page out of a shared notebook drops the access that came from that notebook, while shares on the page itself move with it
- Moving a page to another workspace needs editor rights on both sides
- Owners and editors can share, but never with a higher role than their own. Every share records its creator and only counts while that creator still holds at least the shared role on the page, so removing someone also switches off the shares they made
- A link share gives anyone with the token a role on one page and its subtree, never more than its creator's role, and stops working when the creator loses access
- One function computes the effective role, covered by a table-driven test of every combination. It resolves all grants for a page in one recursive query and caches the result per request; that is the first place to optimise if checks get slow

Phase 1A implements the workspace-membership part (rule 1) behind the single `IPermissionService`, with per-request caching, so shares and ancestor resolution arrive in run 2.2 without touching any endpoint. Every endpoint names a policy (`signed-in`, `admin`) or a role on a workspace or page, and a test fails, naming the endpoint, for any endpoint that does not and is not on the reviewed list of anonymous ones. A caller with no access at all gets 404, so the existence of other people's data does not leak; a caller with too low a role gets 403.

### Compaction guarantees

A page's content is always its snapshot plus the updates after it, and compaction may never change that result.

- **Transactional:** the new snapshot and the removal of merged updates commit in one Postgres transaction, so a crash leaves the old state or the new one, never a mix
- **Watermark:** a snapshot records the highest update sequence number it contains, and never lowers it. Exactly the rows the compaction read are removed, not "every row up to the watermark": sequence numbers are handed out before commit, so a row with a lower number can commit after the read, and deleting by number would lose it. Updates arriving during compaction are untouched
- **One at a time:** a Postgres advisory lock per page prevents two compactions on the same page
- **Idempotent:** running compaction twice gives the same result as running it once
- **Verified:** before committing, the new snapshot is decoded and compared with a full replay of the old snapshot plus its updates; a mismatch aborts and logs an error
- **Safety net:** in phase 1A, merged updates move to an archive table and are kept for 30 days instead of being deleted
- **Tested:** property-based tests with random edits, concurrent clients and injected crashes must always end in identical documents

### Attachments

- Stored by SHA-256 hash, so a file uploaded twice is stored once
- Behind one storage interface: a local volume in phase 1A, S3-compatible storage later without changing callers
- Configurable size limit per file, 50 MB by default. Pages over 5 MB of text stay normal, editable pages but show a warning that they may be slow. Only above a configurable safety ceiling (50 MB by default, protecting the server) is an imported note stored as an attachment instead, and flagged in the import report
- A daily job finds files that no page refers to and deletes them after a 30-day grace period
- Per-user quotas and storage reports arrive with multi-user in phase 2

### Joplin import mapping

The import must lose nothing Joplin users care about, and it must be repeatable. Each imported item keeps its Joplin id, so importing again updates pages instead of duplicating them, which lets you run both apps side by side during the switch. A re-import compares three versions of each note: the one last imported, the one in Joplin now and the one in Inkpact now. Changed only in Joplin: updated. Changed only in Inkpact: left alone. Changed in both: Inkpact keeps its version, the Joplin version is saved as a conflict copy beside it, and the report lists it.

| Joplin | Inkpact |
| --- | --- |
| Notebook and nested notebooks | Page with child pages, same nesting |
| Note (Markdown body) | Page, with the Markdown converted to editor content |
| Internal link `:/noteId` | Link to the imported page |
| Resource (image or file) | Attachment; image links become inline images |
| Tags | Tags (stored in 1A, shown in the UI from 1B) |
| To-do, completed state, due date | To-do fields on the page (UI in phase 2) |
| Alarm | Reminder record (delivered from phase 2) |
| Created and updated times | Kept as the page's timestamps |
| Encrypted notes | Imported decrypted, from a JEX export made in the Joplin desktop app |

Every import ends with a report: counts per type, plus a list of anything skipped or converted imperfectly, such as unsupported Markdown or broken links.

### Database migrations

- EF Core migrations are the single source of truth for the schema; no manual SQL changes
- Pending migrations are applied automatically on startup, the norm for single-instance self-hosted apps. Regular backups are the admin's responsibility; by default the app also writes a `pg_dump` to the data volume before migrating, which stays quick because attachments live outside the database, and a setting turns it off for large installs. A brand-new database is not dumped (there is nothing to lose), and a failing dump stops the upgrade. The image therefore needs the PostgreSQL client tools (`pg_dump`)
- The app refuses to start against a database newer than itself, with a clear message
- Downgrades are unsupported: roll back by restoring the backup taken before the upgrade
- Backups carry their schema version (the migrations history table is part of every dump)
- CI runs an upgrade test: the previous release's database, migrated by the new version, must pass the integration tests

## Project setup

The whole repository is licensed AGPL-3.0, so anyone running a modified version, including as a hosted service, must share their changes. Contributions come in under the same license with a DCO sign-off; no CLA is needed, since there are no paid features to protect.

**Repository layout (monorepo)**

- `server/` — ASP.NET Core API, WebSocket sync hub, EF Core with Npgsql, background compaction worker; `server/sidecar/` is the Node process running Yjs, a pnpm workspace package that Node runs as TypeScript, without a build step
- `web/` — TypeScript client (Vue 3 + Vite + Pinia), TipTap + Yjs editor, shared by web and desktop
- `desktop/` — Electron shell around `web/` (phase 3)
- `deploy/` — `docker-compose.yml` (app, Postgres, optional Redis), Unraid template XML
- `docs/` — architecture notes, sync protocol, self-hosting guide, run cards in `docs/runs/`
- `CLAUDE.md` — build and test commands, conventions and the run rules, kept short so every run loads it cheaply

**Tooling**

- pnpm as the only Node package manager from the first commit: pnpm workspaces for `web/` and `desktop/`, version pinned through the `packageManager` field and Corepack, lockfile committed, `pnpm install --frozen-lockfile` in CI
- pnpm supply-chain settings: dependency install scripts blocked except an explicit `onlyBuiltDependencies` allow-list, and a `minimumReleaseAge` so freshly published (possibly compromised) versions aren't installed straight away
- GitHub Actions for everything; see CI/CD below
- Integration tests with Testcontainers for Postgres; sync tests that run two headless clients against one server
- Semantic versioning from v0.1; changelog from conventional commits

**Your own deployment:** run it on Unraid behind Traefik on a test subdomain from phase 1, and dogfood it next to Joplin before migrating.

## CI/CD

GitHub hosts everything, and every step from pull request to an update on your Unraid server is automated, in the same style as GarageStack. The pipeline is built up over runs 1.02, 1.13, 1.20 and B.8 rather than in one go.

Nothing reaches users unless every check is green, and a release is a single merge of the release PR.

**Repository**

- Branch protection on main: pull requests only, required checks, linear history
- Conventional commits enforced by commitlint; release-please turns them into versions and changelogs
- Issue forms, Discussions, CODEOWNERS, CONTRIBUTING, SECURITY.md with private vulnerability reporting, and a DCO sign-off check on every commit
- Renovate, with automerge only for patch updates of build and dev tooling on an allow-list; runtime dependencies (auth, database, crypto, security libraries) always get a manual review

**Every pull request**

- Server: dotnet format check, build, xUnit unit tests, Testcontainers integration tests, migration upgrade test; the tests start the real Yjs sidecar, so the job has Node and the workspace dependencies
- Client: ESLint (including the vue-i18n rules), Prettier, vue-tsc type check, Vitest; the same checks for the Yjs sidecar package
- End-to-end: Playwright on desktop and phone viewports (and WebKit) against the real server and the built bundle (the container from 1.13); any CSP violation fails the run
- Security: CodeQL for C# and TypeScript, GitHub dependency review, `pnpm audit`, the vulnerable NuGet package check, a Trivy image scan, and a licence check of all dependencies
- Reports posted on the PR: coverage, and bundle size compared with main, with a warning on regressions

**Main and releases**

- Every merge to main publishes an `edge` image to GHCR (amd64 and arm64), handy for your own Unraid test instance
- Docs and screenshots are rebuilt: Playwright captures screenshots from a demo mode with sample data, as in GarageStack
- Merging the release PR tags the version and creates the GitHub Release with its changelog
- Release images are tagged `x.y.z`, `x.y` and `latest` on GHCR, signed with cosign, with SBOM and build provenance attestations
- The Unraid template in your community-apps repository is updated automatically
- The app checks GitHub for newer releases through its own server, so the CSP stays strict, and shows a notice; a setting turns this off
- The performance targets are checked against the seeded data set before each release
- From phase 3: Electron installers for Windows, macOS and Linux are built, signed and attached to the release, with auto-update through electron-updater (code-signing certificates for Windows and macOS cost money yearly; budget them before phase 3)

## Quality standards

Every feature ships with unit tests, every user-facing string goes through i18n, all styling uses Tailwind, and the security baseline holds from the first commit — enforced in CI rather than by discipline.

**Testing**

- Backend unit tests: xUnit with NSubstitute; use Shouldly or plain asserts, since FluentAssertions v8+ needs a paid license for commercial use
- Backend integration tests: Testcontainers with a real Postgres for repositories, permission checks and the sync hub
- Frontend unit tests: Vitest + Vue Test Utils for components, Pinia stores, composables and TipTap extensions
- End-to-end: Playwright on a desktop and a phone viewport, including two clients editing one page and one client going offline
- CI gate: all tests pass. Every feature ships with tests, and the critical paths (sync, compaction, tree operations, permissions, import, sanitising) need thorough ones; coverage is reported on every PR but is not a gate, so tests follow risk rather than a percentage
- Phase 0 code is throwaway; its sync scenarios become the first integration tests in phase 1A. Compaction and tree operations also get property-based tests with FsCheck: random edit, move and crash sequences must always converge without data loss

**Internationalisation**

- English and Dutch from the first commit; English is the fallback language
- Client: vue-i18n with one JSON file per language; no hard-coded strings, enforced by the vue-i18n ESLint plugin (missing or unused keys fail the build)
- Server: API errors return codes that the client translates; emails and other server texts use IStringLocalizer with .resx files
- Dates, numbers and file sizes formatted with `Intl` in the user's locale
- Language follows the browser, with a per-user override in settings

**Styling**

- Tailwind CSS v4 with design tokens (colours, spacing, typography) defined once in the theme
- Mobile-first breakpoints; dark mode through the `dark` variant
- Headless, accessible components (Reka UI) styled with Tailwind; no separate component-library CSS

**Security from day one**

These are in place in phase 1A, because each is painful to add once code and data exist.

- **Strict CSP:** `default-src 'self'`, no `unsafe-inline` or `unsafe-eval`, no nonces or hashes, `frame-ancestors 'none'`, `object-src 'none'`, `base-uri 'none'`, `connect-src 'self'`, which also covers the app's own WebSocket. Vue's runtime-only build and Tailwind's static CSS need no exception. `upgrade-insecure-requests` is deliberately left out, because it blanks every page of an install served over plain HTTP on a LAN address. The ASP.NET Core app sets the policy on every response, including errors and 404s; a CI end-to-end test (Chromium, Pixel 7, iPhone 15 on WebKit, and plain HTTP on a non-localhost name) fails on any CSP violation or uncaught page error. The exact value is pinned in a server test, so loosening it shows up in review
- **Other security headers:** HSTS (set in Traefik), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, a restrictive `Permissions-Policy`, `Cross-Origin-Opener-Policy` and `Cross-Origin-Resource-Policy: same-origin`, `X-Frame-Options: DENY`, and no `Server` header
- **Content sanitising:** pasted HTML, imported Markdown and rendered content pass through an allow-list sanitiser (DOMPurify on the client, a matching server-side check on import), so stored XSS can't reach shared notes later
- **Safe attachment serving:** files served with `Content-Disposition: attachment` (images excepted), their own sandboxing CSP and `nosniff`; SVG never rendered inline. Notes reference attachments by id, never by URL, so moving them to a separate domain or S3 later needs no content migration
- **Deny-by-default authorisation:** every endpoint and every WebSocket connection goes through the one permission service, even while there is a single user; a test fails for any endpoint without an explicit policy
- **WebSocket hardening:** authenticated by the same session cookie, Origin header checked against the app's own (a missing one is refused), per-connection message size and rate limits, a cap on sockets per user and pages per socket, bounded send queues, and the role checked on every subscribe (viewers read, editors write). Logout and logout everywhere close the session's sockets at once; a periodic check catches sessions that ended otherwise
- **Sessions:** standard ASP.NET cookie authentication with a Postgres-backed session store (ITicketStore): the cookie holds only a session id, so logout everywhere, revoking one session and removing a user take effect on the next request, without a custom session system. 30 days, sliding. Cookies are `HttpOnly` and `SameSite=Lax`. Whenever the browser is on HTTPS (also behind Traefik, through `X-Forwarded-Proto` from a trusted proxy) the cookie is `Secure` and named `__Host-inkpact`; on plain HTTP, which many self-hosters use on a LAN address, it is named `inkpact` without `Secure`, because a browser refuses a `__Host-` cookie there. `Auth:CookieSecure` can force the flag on (`true`) or off (`false`). State-changing API calls must also be `application/json`, which a cross-site form cannot send
- **CSRF:** every state-changing API call (POST, PUT, PATCH, DELETE under `/api`, login and setup included) carries an ASP.NET Antiforgery token in the `X-CSRF-Token` header. `GET /api/auth/state` hands it out, tied to whoever is signed in at that moment, so the client asks again after every login and logout and repeats a call once when the server finds its token out of date. Its cookie (`inkpact-csrf`, `HttpOnly`, `SameSite=Strict`) has no `__Host-` prefix, because Antiforgery uses one fixed cookie name; the token's tie to the user and the JSON-only rule make that harmless
- **Yjs sidecar:** the only code that decodes Yjs updates, which are bytes a user sent. It has no network port, an empty environment, `--disallow-code-generation-from-strings`, a heap limit, a timeout on every call (a stuck request kills the process) and Node's permission model: read access to its own folder only, no writes, no child processes. A Yjs bug reached with crafted bytes should therefore not be able to read the server's configuration, or its environment through `/proc`
- **Passwords:** Argon2id (libsodium; 46 MiB, one iteration, as the OWASP cheat sheet gives first) as an encoded string that is upgraded at login when the parameters change; 12 to 128 characters and no composition rules; an unknown email costs the same time as a wrong password and gets the same answer
- **Login throttling:** one address may make 10 login or setup attempts a minute (429, `Retry-After`). Per email address, five wrong passwords lock further logins for a minute, doubling with every further failure up to 15 minutes, and a success or a day without failures starts the count again. An address without an account is counted and locked exactly like one with an account, so the lockout does not show who is registered; its key is a hash, so typed addresses are not kept. The accepted cost: anyone can keep a known address from *new* logins for a while (sessions that exist keep working)
- **ASP.NET Data Protection keys:** persisted to the data volume, so restarts don't log everyone out. They are not encrypted by default: under the trusted-administrator model anyone who can read the volume can read every note, so a certificate kept next to the keys would add nothing. `DataProtection:CertificatePath` encrypts them for installs that keep the certificate elsewhere
- **First-run setup:** the first account is created on the setup page and becomes the administrator, but only with a setup code that the server writes to its log at startup (or `Auth:SetupCode`), so whoever reaches a fresh internet-facing install first cannot take it over. Setup is closed for good once an account exists
- **Security audit log:** setup, logins (succeeded, failed, locked), logout and logout everywhere, and (from phase 2) shares, in the `audit_events` table: time, type, user, the address that was typed for failed logins, IP address and user agent. Each event is written when it happens and a failing write fails the request. There is no way to read it yet (admin page, run 2.9) and no retention yet (run B.7)
- **Supply chain:** Renovate for dependency updates (until then Dependabot moves the exact .NET SDK pin in `global.json`, and its pull requests skip the commit-message and sign-off checks, which a bot cannot satisfy); `dotnet list package --vulnerable`, `pnpm audit`, CodeQL and a Trivy scan of the container image in CI; the container runs as a non-root user with a read-only root filesystem

**Encryption at rest** is left to the host (disk or volume encryption); Inkpact does not encrypt its database or files itself, and end-to-end encryption remains a non-goal.

**Trusted administrator:** whoever runs the container is trusted. Anyone with access to the database or the attachments volume can read all data, and Inkpact does not try to protect data from the server owner; this follows from leaving end-to-end encryption out of scope.

## Performance targets

Targets hold on your Unraid server over the LAN, against a seeded test set of 10,000 pages and 2 GB of attachments, checked by an automated Playwright run before each release.

| Measure | Target |
| --- | --- |
| Open a page (95th percentile) | under 500 ms |
| Search, first results (95th percentile) | under 200 ms |
| Remote edit visible on another device | under 1 s |
| App usable on a phone from a cold start | under 2 s |
| JavaScript bundle (editor lazy-loaded on first page open) | no hard limit; size tracked in CI, regressions warned |
| Joplin import of 5,000 notes | under 5 minutes |
| Server memory, idle, with 10,000 pages | under 500 MB, so it fits a 2 GB VPS next to Postgres |

## Risks and open decisions

The biggest risk is scope and maintenance load, not technology; every phase must end in something you use daily.

| Risk | Mitigation |
| --- | --- |
| Scope creep toward "Notion clone" | Non-goals list above; feature requests wait until after v1.0 |
| Yjs decoding in .NET is immature | Server stores opaque blobs; a Node sidecar running Yjs does compaction, diffs and text extraction (decided in spike 0.4) |
| Mobile editing on TipTap has rough edges (selection, virtual keyboard) | Test on your own Android phone from phase 1, not at the end |
| Second long-term project next to GarageStack | Ship small phases, keep CI and releases automated, recruit contributors early |
| Storage growth from update logs | Compaction job and history retention from the first release |
| Pro usage limits slow progress | Work in runs that fit one window; Opus only for planning and review of critical runs; logs summarised by the local model; upgrade the plan only if runs regularly stall on limits |
| Sonnet gets a critical path subtly wrong | Every critical run is planned or reviewed by Opus; property tests on sync, compaction and tree operations catch what review misses |

**Deferred on purpose, each with a trigger**

- Metrics endpoint (OpenTelemetry, Prometheus format) with open WebSocket connections, sync latency, compaction duration and database query timings: before the first public release, earlier if a performance target slips
- Password reset by email: before the first public release; CLI reset until then
- Threat model document: before the first public release
- Full accessibility audit: before v1.0, on top of the baseline in 1A
- Search beyond Postgres full-text (fuzzy matching, better ranking): when search annoys you in daily use
- Single server only: stated in the README; clustering is out of scope
- Trash retention: 30 days by default, configurable
- Backup targets: lose at most one day of data and restore within an hour, using nightly pg\_dump plus the attachments volume

**Open decisions**

- [x] Frontend framework: Vue 3 (decided)
- [x] Editor UI: TipTap with Notion-style block handles (decided)
- [x] Data access: EF Core (decided); raw SQL only where a recursive query needs it
- [x] Auth: local accounts in 1A, OIDC in phase 2 (decided)
- [x] Attachment storage: local volume behind a storage interface from day one, S3 implementation later (decided)
- [x] License: AGPL-3.0 for the whole repository, DCO sign-off for contributions, no paid features (decided)
- [x] Build process: runs sized to one Pro usage window; Sonnet by default, Opus for critical design and review, no Fable (decided)
- [x] Project name: Inkpact (decided); still to claim the GitHub org, Docker Hub and GHCR names, npm and NuGet prefixes, and a domain
