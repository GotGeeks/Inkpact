# Run B.6: Installable PWA and offline Yjs persistence

**Model:** O·high → S·med in the roadmap. The sync and offline-storage plan is a critical path; review the plan or finished code at the roadmap's requested level before the PR merges.

**Status:** Implemented on `feat/b6-pwa-offline`; the critical sync/storage review blockers are fixed and the full automated check passes. The manual device test pass remains; this run does not make tree changes or every app feature available offline.

## Goal

Install Inkpact as a Progressive Web App on supported browsers, open the app shell without a network connection after it has been cached, and preserve edits to previously opened pages in IndexedDB across reloads. On reconnect, the ordinary sync protocol converges the local and server documents without duplicating or losing work.

Local persistence is recovery between visits, not proof that the server has saved an edit. Do not claim an edit is synced until the existing sync client confirms it.

## Inputs

- [`docs/architecture.md`](../architecture.md): the B.6 row, "Sync flow" (especially the fixed first paragraph, reconnect and lost-edit rules), and the offline-storage rule.
- [`docs/runs/1.11.md`](1.11.md): the sync client, unsent edits, disconnect/reconnect, and the explicit IndexedDB work deferred to B.6.
- [`docs/runs/1.21.md`](1.21.md): offline copies, idempotent page creation, size limits and retry behavior.
- [`docs/runs/B.5.md`](B.5.md): history's offline and restore safety boundaries; B.6 must not weaken them.
- [`docs/runs/0.6.md`](0.6.md) and [`docs/spike-findings.md`](../spike-findings.md): phone reconnect experience and the fact that HTTPS, service workers and iOS were not covered by the original spike.
- [`docs/design/inkpact_slate/DESIGN.md`](../design/inkpact_slate/DESIGN.md): sync-status and notification tokens.
- [`docs/test-pass.md`](../test-pass.md): append the manual install and offline checks below; defer device-specific checks to run 2.10b.
- Existing code:
  - [`index.html`](../../web/index.html), [`vite.config.ts`](../../web/vite.config.ts), [`main.ts`](../../web/src/main.ts) and [`router.ts`](../../web/src/router.ts);
  - [`auth.ts`](../../web/src/stores/auth.ts), [`tree.ts`](../../web/src/stores/tree.ts), [`sync.ts`](../../web/src/stores/sync.ts) and [`syncClient.ts`](../../web/src/sync/syncClient.ts);
  - [`PageView.vue`](../../web/src/pages/PageView.vue), [`PageEditor.vue`](../../web/src/editor/PageEditor.vue) and [`firstParagraph.ts`](../../web/src/editor/firstParagraph.ts);
  - [`offline-copy.spec.ts`](../../web/e2e/offline-copy.spec.ts), [`editor-sync.spec.ts`](../../web/e2e/editor-sync.spec.ts), [`sync.spec.ts`](../../web/src/sync/syncClient.test.ts) and [`useKeepLostEdits.test.ts`](../../web/src/sync/useKeepLostEdits.test.ts);
  - [`SecurityHeaders.cs`](../../server/src/Inkpact.Api/Security/SecurityHeaders.cs) and the static-file/fallback configuration in [`Program.cs`](../../server/src/Inkpact.Api/Program.cs).

## Before this run

1. The built SPA is served by ASP.NET Core. Hashed assets can be cached, and an unknown `/api/...` route returns a JSON 404 rather than the SPA shell. There is no manifest or service worker.
2. The security policy already permits same-origin workers and manifests. Keep `script-src`, `worker-src`, `manifest-src` and `connect-src` strict; do not add inline code or external hosts.
3. The editor creates its Yjs document in the sync client, applies the fixed first-paragraph update, and subscribes to the server. Documents and edits are otherwise held in memory. An unsent edit triggers a `beforeunload` warning.
4. On reconnect, sync compares Yjs state and merges with the server; if a Sync deletes offline edits, the client can preserve the prior document as a copy. B.6 must keep this behavior, including the distinction between local persistence and server confirmation.
5. Router authentication bootstrap and page/tree metadata currently require the server. A service-worker shell alone cannot reopen a page offline after a reload; a narrowly scoped, account-isolated local bootstrap and metadata cache are needed for that journey. The server remains authoritative for authorization and tree operations.

## Decisions

1. **Installability and supported contexts.** Add a same-origin web app manifest, app name, local icons and theme metadata. Register a same-origin service worker. Installation and service workers require a secure context: HTTPS in production (localhost is the browser exception). An HTTP LAN install stays usable online but must not claim it is installable or has offline caching. Chromium's install prompt and iOS's manual Add to Home Screen are different; do not promise a universal prompt.
2. **Cache only the application.** Cache the app shell and same-origin, content-hashed static assets needed to run it. An offline navigation to an app route may fall back to the cached shell, but unknown API routes must remain JSON 404s. Never cache `/api`, WebSockets, authenticated responses, attachments, or note content in the service-worker Cache API. Use versioned caches and a safe update/cleanup strategy so a new shell cannot strand an open tab on missing hashed assets. First use still requires a successful online visit.
3. **Persist Yjs state before syncing.** Store the document state and the minimal page metadata needed to reopen a previously opened page. Namespace data by the authenticated user id, workspace id and page id; never infer identity from an email or a stale cookie. Hydrate and finish local persistence before making the document editable or subscribing it to the network. Apply the fixed first-paragraph update idempotently and only in the established initialization path. Remote changes may be persisted locally too; reconnect must still use the existing Yjs sync rather than treating a local copy as server truth.
4. **Define offline access narrowly.** A person may reopen and edit a page already cached for their current browser profile and last successfully authenticated account, including by reloading its known page URL while offline. Do not offer an offline page tree, search, login, workspace changes, imports, uploads or other server mutations. Do not cache authentication responses or credentials. A cached user id selects a local partition only; it is not authorization. On reconnect, revalidate the session and page permission before sending changes. If identity cannot be safely matched, keep pending local edits isolated and visible; never sync one account's document as another account.
5. **Keep local recovery honest.** Show distinct local-persistence, unsynced and server-synced states. Retain the unload warning until an edit is durably recorded locally (or confirmed by the server); surface IndexedDB open, blocked, quota and write failures rather than silently pretending data is safe. Request persistent browser storage only where supported and treat it as best effort, not a guarantee. Never evict unconfirmed edits or an offline-copy recovery document to make room. Define a bounded cleanup policy for server-confirmed cached documents, and make any loss of offline availability explicit. Do not cache attachment bytes; an attachment unavailable offline must not break the rest of the page.
6. **Keep updates safe.** Do not force-reload clients when a service-worker update is ready. Defer activation/reload while a document has unconfirmed edits, and tell the person when it is safe to update. Signing out clears the active offline identity; it must not replay that account's pending documents into a later session. A later sign-in may recover only the matching account's local partition. Do not claim IndexedDB is encrypted or protected against another person with access to the browser profile.
7. **No unapproved dependency.** Prefer browser APIs and existing Yjs capabilities if they meet the persistence and ordering guarantees. If implementation needs `y-indexeddb`, a PWA plugin or any other package, ask before changing manifests or the lockfile and explain why it is necessary. Do not add a package to `onlyBuiltDependencies`.

## Done when

- The manifest and local icons pass installability checks on supported secure-context browsers; the service worker installs, updates and serves the shell offline without weakening CSP.
- After a page has been opened online, edits made offline survive closing/reloading the tab and reopening that page URL in the same profile. A page never cached locally is honestly unavailable offline.
- The editor does not become writable until local hydration is complete. A local edit, a server edit, and reconnect in either order converge exactly once; the fixed first paragraph remains singular.
- Online confirmation, local durability and unsynced status are distinguishable. Failed or blocked IndexedDB writes keep an appropriate warning and never produce a success-shaped fallback.
- Previously opened offline state cannot be read or sent under a different user. Logout, expired sessions, permission revocation and account switching have explicit behavior; server authorization remains authoritative.
- Service-worker updates do not strand an open page or force-discard unconfirmed edits. API errors, auth state, WebSockets, attachments and user content are never served from a Cache API entry.
- Existing lost-edit copy, sync, compaction, history and CSP behavior continue to pass. English and Dutch strings are provided for all new user-visible statuses and failures.

## Build order

1. Confirm the offline reload and identity boundary against the existing auth, router, page metadata and sync flow. Establish how a cached route finds its local user partition without treating local identity as server authorization.
2. Choose the IndexedDB persistence mechanism and its lifecycle. If a third-party package is needed, stop and ask for approval before adding it. Test hydration ordering, concurrent tabs, quota errors, identity isolation, and the distinction between stored locally and synced.
3. Add the manifest, same-origin icons and service worker with a deliberately small asset-only cache. Verify fallback routing cannot answer API paths and ensure cache updates preserve already-open clients.
4. Integrate persistence with the sync client and editor initialization; add the offline cached-page bootstrap, safe reconnect/auth revalidation, local storage status and both translations. Preserve the existing beforeunload and lost-edit recovery semantics where persistence is unavailable or still pending.
5. Add real-browser service-worker/IndexedDB journeys and accessibility coverage. Update this card and the test-pass checks. Change `docs/architecture.md` only if implementation proves its frozen offline rule wrong, in the same PR.

## Tests

- **Vitest:** IndexedDB hydration before editor initialization and sync; first-paragraph idempotence; local and remote updates; duplicate replay; user/workspace/page isolation; logout and account switching; identity mismatch; unavailable, blocked, failed and quota-exceeded storage; persistence status, retries and unload warning; safe eviction and pending-edit protection.
- **Sync and sidecar:** extend the existing sync-client and offline-copy scenarios so edits survive a reload while offline, then converge on reconnect exactly once. Include remote changes while the page is closed, a deleted-block edit recovered as a copy after reload, oversized data, retries, and two tabs of the same account. Do not skip formatting or deletion cases already covered by the sync rules.
- **Service worker:** verify install and update, cached-shell navigation, hashed assets, offline fallback, old/new cache cleanup, safe deferral while edits are pending, and CSP. Assert `/api/auth/state`, other API paths, WebSockets, attachment routes and user content are never returned from the worker cache. Confirm unknown `/api/...` requests remain JSON 404s.
- **Playwright:** use the built production app and real server. On desktop and phone, open a page online, cut the network, edit, reload its known URL, verify the local text and local-only status, reconnect, and verify another client sees the edit once. Cover missing local pages, failed authentication/revoked access, logout/account switching, storage failure, and a waiting service-worker update. Keep existing sync, lost-edit and CSP specs enabled; include the 320-pixel accessibility project and both themes.
- **While building:** run changed Vitest files, web lint/typecheck and affected Playwright specs. Before each push run `pnpm check:push` plus the affected Playwright specs; before the last push run `pnpm check` once with Postgres up. Never weaken or skip a failing test.

## Docs to update

- [`docs/test-pass.md`](../test-pass.md): add the HTTPS install, offline reload and reconnect checks below; these device-specific checks remain deferred to run 2.10b.
- [`docs/self-hosting.md`](../self-hosting.md): explain HTTPS/secure-context requirements, first-online-visit caching, local storage behavior and browser limitations.
- This card: replace the planned status with the implementation handoff, exact test results, deviations and remaining work.
- [`docs/architecture.md`](../architecture.md): edit only if implementation proves the frozen offline behavior wrong, in the same PR.

## Out of scope

- Offline tree operations, creating or moving pages, trash, tags, favorites, search, imports and background synchronization of non-document changes.
- Offline authentication, cached credentials, caching API responses or attachment bytes, and any claim that browser storage is encrypted or guaranteed against browser eviction.
- Push notifications, background sync, offline attachment uploads, multi-user sharing changes and the desktop SQLite store.
- Arbitrary offline browsing of pages never opened and cached on this browser profile.
- New dependencies without the user's approval.

## For the test pass

- On the laptop over HTTPS, open a page online, install Inkpact if the browser offers installation, disconnect the network, edit, reload the same page URL and confirm the edit remains with a local-only/offline status. Reconnect and verify the edit reaches a second client once.
- Repeat on an Android phone. On an iPhone, use Add to Home Screen rather than expecting a Chromium-style install prompt; check the same offline reload and reconnect flow.
- Open a page that has never been loaded on the device while offline and confirm Inkpact explains it is unavailable rather than showing an empty page. Confirm tree changes and attachment uploads remain unavailable offline.
- With a pending offline edit, let a newer app version become available. Confirm it does not force a reload or discard the edit; update only after the pending change is safe.
- In a spare test account/profile, sign out and sign in as another account. Confirm the prior account's cached notes or pending edits are not shown or sent for the new account.
- In a disposable multi-user workspace when available, downgrade or revoke access while an offline edit is pending. Confirm it stays on the device and is not sent; restore edit access and recover it as the original account. Confirm a viewer can read a previously confirmed page but cannot edit it.
- If an HTTP LAN installation is still used, confirm it continues to work online and that Inkpact does not falsely claim service-worker or installation support there.

## Handoff

- **Scope:** The app has a same-origin manifest, local icons, an asset-only service worker and an offline bootstrap for previously opened pages. The worker precaches the built shell and lazy assets, not API responses, attachments or notes. It does not force a reload and retains old shell caches while any window is open, including an uncontrolled first-visit tab; a later activation with no open windows cleans them up. Plain HTTP on a LAN remains online-only.
- **Local durability:** [`indexedDb.ts`](../../web/src/offline/indexedDb.ts) and [`offlineDocument.ts`](../../web/src/offline/offlineDocument.ts) partition page metadata and per-writer Yjs snapshots by user, workspace and page. Normal hydration and its initial transaction finish before the editor or network sees the document. If IndexedDB is initially unavailable, the editor can continue in memory; when storage recovers, that live document is adopted and persisted rather than replaced. Recovery copies commit before a snapshot that could remove offline work. Local edits remain unconfirmed until a complete Sync or a pong after an update. The v1-to-v2 database upgrade conservatively marks existing snapshots unconfirmed. Cleanup keeps up to 40 ready, server-confirmed pages without recovery copies across the profile and protects every unconfirmed writer and pending recovery copy; eviction is reported.
- **Account and permissions:** A cached identity selects a local partition, not server authorization. Account-change events gate the app even in tabs without a sync client; pending edits stay isolated and are only resumed after the same account is verified. Reconnect revalidates the session and page access, and blocked or missing pages are authorized again before resubscription. Signing out attempts to clear the active offline identity but does not erase cached documents.
- **Review blockers:** The storage-recovery document mismatch, clientless-tab account-change gap, blocked/missing-page reauthorization, uncached-page reconnect, first-uncontrolled-tab cache cleanup, and redundant-worker installation notice now have regression coverage. The favorites end-to-end journey also waits for server persistence before intentional reload and for server list updates after trash/restore.
- **Verification:** `pnpm check:push` passed: lint, type-checking, formatting, **1,529 web**, **152 sidecar** and **2,060 non-slow server** tests. The final `CI=true pnpm check` passed, including **2,064 server tests** (slow tests included) and **466 Playwright passes**; 83 browser cases are intentionally skipped and one unrelated import case passed on its configured retry. The full browser run covered the storage recovery, account boundary, permission restoration, uncached-page reconnect and service-worker lifecycle fixes.
- **Limits and follow-up:** Physical-device, HTTPS, proxy and install checks remain deferred to [`docs/test-pass.md`](../test-pass.md). Offline page editing remains limited to previously opened pages and browser storage is not a backup.
