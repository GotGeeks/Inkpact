# Run B.5b: The rest of version history: capture, the history panel, restore and the owner's setting

**Model:** O·high → S·high in the roadmap. Planned on Fable 5.2 on 2026-11-12 from a reading of #122's code against the B.5 card. Sync, restore and the client's lost-edit check are critical paths: `/code-review` at high on PRs 3 and 4 before they merge, at medium on the others.

**Status:** Planned. Five PRs, in the order below. Nothing is built.

This card plans what [`B.5.md`](B.5.md) left open. That card stays the statement of what version history must do (its "Decisions", "Done when" and "Tests" hold unless this card says otherwise); this one says how, in which order, and where the plan narrows or changes it.

## Goal

A changed page gets versions without anyone asking. A person can open a page's history, look at an earlier version, and put it back without losing what is there now or what someone else is typing. The workspace's owner can change how much is kept. After PR 2 history can be looked at; after PR 5 it can be restored and configured.

## Inputs

- [`B.5.md`](B.5.md): all of it, and its handoff.
- [`docs/architecture.md`](../architecture.md): the B.5 and B.5b rows, "Compaction guarantees", "Tree and metadata conflicts", "Permission resolution", the sync flow (steps 2 and 7).
- [`docs/sync-protocol.md`](../sync-protocol.md) and [`docs/design-notes/offline-copy.md`](../design-notes/offline-copy.md): PR 3 changes both.
- [`docs/design-notes/import-reimport.md`](../design-notes/import-reimport.md): the import's replacement, which PR 3 also makes safer.
- Code read while planning:
  - [`History/`](../../server/src/Inkpact.Api/History/): the snapshot builder, the retention job and worker, the policy service and endpoints, the workspace history lock; [`PageVersion.cs`](../../server/src/Inkpact.Data/Entities/PageVersion.cs);
  - [`SyncHub.cs`](../../server/src/Inkpact.Api/Sync/SyncHub.cs) (`PublishAsync`), [`PageUpdateStore.cs`](../../server/src/Inkpact.Api/Sync/PageUpdateStore.cs), [`IYjsCodec.cs`](../../server/src/Inkpact.Api/Crdt/IYjsCodec.cs) (`ReplaceAsync`, `ReadAsync`), [`CompactionOptions.cs`](../../server/src/Inkpact.Api/Compaction/CompactionOptions.cs);
  - [`syncClient.ts`](../../web/src/sync/syncClient.ts) (`handle`, `handleSync`, `unsyncedItems`), [`protocol.ts`](../../web/src/sync/protocol.ts), [`useKeepLostEdits.ts`](../../web/src/sync/useKeepLostEdits.ts).
- Not read while planning, to be read by the PR that touches them: `Compactor.cs` and its worker's selection query, `ImportRunner.WriteReplacementAsync`, `TreeService` (rename), the offline store, `PageEditor.vue` and the editor's extension list.

## Review of #122 (the foundation that is merged)

Read against the B.5 card. Nothing in it is wrong enough to redo; five things have to change before it can carry captures.

1. **The snapshot builder does not say how far it read.** `HistorySnapshot` has the state and the title but not the page's last sequence number (`PageContent.LastSeq`) nor the replay's fingerprint. Capture needs both: the sequence number to know what is already captured, the fingerprint to skip a version that would equal the last one. Add them (PR 1).
2. **One capture is four sidecar calls that each replay the whole page:** `BuildAsync` for the first paragraph (the same bytes every time), `CompactAsync`, and `ReadAsync` twice. The sidecar takes one request at a time, so while a large page is captured, every subscribe waits and falls back to the stored log after `Sidecar:DiffTimeout`. Build the first-paragraph update once and keep it; capture one page at a time; and leave a page alone while it is over `Sync:MaxDiffBytes` of live updates (the compactor gets to it first, after which a capture reads one snapshot). Measure a capture of an 8 MiB page in PR 1 and write the number in the handoff.
3. **`PageVersion` has no fingerprint and no record of what it covers.** Add `fingerprint` (the replay's) and `content_seq` (the builder's last sequence number) in PR 1's migration. Existing rows: none exist, since nothing captured.
4. **The retention worker is off** (`History:RetentionEnabled` false). PR 1 turns it on, in the same PR that first writes versions.
5. **The policy endpoints work and are tested, but nothing shows them.** `GET` needs the viewer role and `PUT` the owner. PR 5 builds the screen on them unchanged.

Not checked in this review: the retention rule's bucket arithmetic beyond reading it (its tests were not run for this plan), and the migration's SQL.

## Decisions

### 1. Capture (PR 1)

- **What is still to capture is worked out from what is stored, with one small table for the cursor.** `page_history_state` (page id as key): `captured_seq` (the `content_seq` of the page's newest version), `failed_attempts`, `next_attempt_at`, `failure` (a code: `sidecar_unavailable`, `unreadable`, `full`), all nullable or zero. No write on the edit path: an update only appends to `page_updates`, as today. A page has something to capture when its last stored sequence number (the snapshot's watermark or its newest live update) is past `captured_seq`.
- **When.** A background worker (`HistoryCaptureWorker`, every 30 seconds, as the compactor polls) takes pages that have something to capture and are either idle (their newest live update is older than `Compaction:IdleAfter`, two minutes) or have been changing for long (their oldest update past `captured_seq` is older than five minutes). Oldest first, a bounded number per round (`History:CapturesPerRound`, 20), one at a time. Only in workspaces whose policy has history on.
- **How.** Build the snapshot (the merged builder, with the changes above). Then, in one transaction under the workspace's history lock: read the policy again (off: write nothing); skip when the fingerprint, title and file text equal the page's newest version; make room under the cap by removing the oldest unprotected versions, and when that is not enough, write nothing and record `full`; insert the version (kind `Automatic`, no creator) and move `captured_seq`. A page that changed while it was captured simply has something to capture again.
- **Renames.** The rename's own transaction (in `TreeService`) inserts a row into `page_title_changes` (page, the new title, when, who): one insert, no decoding. The capture worker turns each row into a version of kind `Rename` with that title and the body as it is when the worker gets to it, then deletes the row, in the same transaction as the version. So two renames in a minute leave two versions, each with its title. **This narrows the B.5 card:** the body of a rename version is the body at capture, which may be a little later than the rename. The alternative (decoding the page inside the tree's transaction) would hold the workspace's tree lock for a sidecar call.
- **Failures.** The sidecar is down: nothing is recorded as failed, the round ends, the next round tries again. A page that cannot be read (the builder's verification fails, or anything is skipped): `unreadable`, and the next attempt waits with a doubling backoff from an hour to a day, as the compactor's does. Logged with the page id and the code only.
- **No history from before.** A page gets its first version at its first capture after this PR. An existing library is captured in the background at the worker's pace (20 pages per 30 seconds: 5,000 pages take about two hours), oldest change first.

### 2. Reading history (PRs 1 and 2)

- `GET /api/pages/{pageId}/versions?before=<cursor>&limit=`: newest first, at most 50, metadata only (id, created at, kind, title, creator when known, size), a cursor of created-at and id, and the page's history state (`on`, `off`, `pending` when something is waiting to be captured, `failed` with its code, `full`). Viewer role on the page.
- `GET /api/pages/{pageId}/versions/{versionId}`: the version's title and its content as the editor's document (the sidecar's `ReadAsync` on the stored state). The version is looked up within the page. `503 history_unavailable` when the sidecar does not answer; `409 version_unreadable` when anything was skipped.
- **The panel (PR 2).** A "History" action in the page's header opens a dialog (full screen on a phone, a side sheet from `md`): the list on one side, the selected version's title and content on the other. The content is shown by a second, read-only editor instance built from the same extensions as the page's editor, given the JSON: no Yjs, no socket, so the page that is open is not touched. Links and pictures go through the same checks as in the live editor (`links.ts`, attachments by id). Lazy-loaded with the editor's chunk. States: empty ("no versions yet"), pending, failed, off, loading, error with "Try again" (`AppNotice`, `AppButton`).
- In PR 2 the panel has no Restore button. It arrives in PR 5.

### 3. A replacement that takes nobody's typing (PR 3)

This is the part the B.5 card said not to assume. What the code does today:

- `SyncHub.PublishAsync` stores a server-made update and relays it as an ordinary **Update**. A client applies an Update without looking at what it deletes.
- A client checks for lost edits only on a **Sync** (`handleSync`): the items it made that the server's state vector does not cover, content only; when applying the Sync deletes one, the page as it was is kept as a copy (sync flow, step 7).
- So someone typing in a paragraph at the moment a replacement is published loses that text without a word: their update reaches the server after the replacement and merges into a deleted block. **The Joplin re-import has this gap today**, not only a future restore.

The plan:

- **`PublishAsync` relays a Sync, not an Update:** the server's state vector right after the replacement (`YjsReplacement.StateVector`) and the replacement as its one update. Every client that has the page open already handles a Sync at any time, runs its lost-edit check against that state vector, and sends what the server lacks. An update that was in flight is not covered by that state vector, so its text is found, and kept as a copy when the replacement deleted its block. This needs no new client: **a tab that still runs the old client is protected for text the same way.**
- **A new message before it, `Replaced` (type 8, server to client, the page id and no payload),** tells a client that the Sync that follows is a replacement of the whole content. A client that knows it keeps a copy when it has **any** change the server has not confirmed (text, formatting or a deletion), whatever the Sync deletes. An old client ignores a type it does not know (`decodeFrame` returns null for it), so it gets the text protection only.
- **What stays unprotected, on purpose, and is said in the docs:** a formatting-only or deletion-only change that had not reached the server, on a tab still running the old client, or in an offline document opened days later (B.6), where no `Replaced` message exists any more. Such a change removes or styles text that the replacement removed anyway; no text is lost, and for a restore the state before it is kept as a version. **This narrows the B.5 card**, which asks for every one of these cases to be recoverable.
- The copy is the existing one (a new page beside the original, announced). When it cannot be made, the existing rules of step 7 hold.
- `docs/sync-protocol.md`, the offline-copy note and the architecture's step 7 change in this PR.

### 4. Restore (PR 4, server)

- `POST /api/pages/{pageId}/versions/{versionId}/restore` with `{operationId, expectedSeq, expectedVersion}`: the page's last sequence number and metadata version as the preview saw them. Editor role, CSRF, the page live (not in the trash, nor below one), history on.
- **Order of locks,** outermost first: the hub's page lock (`PublishAsync`), then one database transaction that takes the workspace's tree lock (for the title, through `TreeService`'s rename rules), then the workspace's history lock. The compactor's page lock is not taken: the restore appends an update, as a client does, and the compactor's guarantees already cover an append.
- **Before the locks:** read the target through the sidecar (refuse when anything is skipped), check that every attachment it names still exists on the page, and build the replacement against the page as stored (`ReplaceAsync`). Refuse when the replacement is over `SyncOptions.MaxPageBytes`.
- **Inside, all or nothing:** the page's last sequence number and metadata version are still the expected ones (else `409 page_changed`, nothing written); the operation id is new (a known one returns its first answer; a known one with another request is `409 operation_mismatch`); room under the cap for the safety version (else `409 history_full`); then the safety version (kind `BeforeRestore`, the exact stored state and title before, protected for 24 hours), the replacement update, the title (when it differs, as a rename with its tree version), a version of kind `Restore`, the search text and the backlinks from the replacement's answer, `file_text` from the target, and the operation's row (`page_restores`: operation id, page, target, the two version ids, when, who).
- A restore is an edit made here: the import's comparison base is not touched, so the next Joplin import sees the page as changed here.
- Errors as codes: `version_not_found` (pruned, or of another page: 404), `page_changed`, `page_in_trash`, `history_off`, `history_full`, `version_unreadable`, `attachment_missing`, `content_too_large`, `history_unavailable` (the sidecar).

### 5. Restore in the interface and the owner's setting (PR 5)

- The panel gets "Restore this version" for an editor on a live page with history on: a confirmation that names what is replaced (the content and the title) and what is not (place, tags, favorites, to-dos), one request at a time, and on `page_changed` a notice with "Refresh" that keeps the preview. After a restore the panel closes, the page shows the restored content through sync, and a notice says the earlier state is in the history.
- **A settings screen, `/settings`,** reached from the user menu, with one section for now: the workspace's history (on or off, the three windows, the cap in MiB, what is used). An owner can save; anyone else reads. A warning before a shorter window or a lower cap is saved. Run B.9 (account settings) adds its sections to the same screen (review finding 5.6).
- `docs/self-hosting.md`: the retention controls, what the cap counts, and that history is not a backup. `docs/test-pass.md`: the B.5 card's checks. Both cards' handoffs.

## The five PRs

| PR | Delivers | Size | Critical |
| --- | --- | --- | --- |
| 1 | Capture: the migration (`page_history_state`, `page_title_changes`, two columns on `page_versions`), the worker, rename rows, the cap at capture, retention on; the list and preview endpoints | Large | Yes |
| 2 | The history panel, read-only: list, preview, states; both languages; a11y and Playwright | Medium | No |
| 3 | `PublishAsync` relays a Sync, the `Replaced` message, the client's check for unconfirmed changes; the import's replacement gets the same protection | Medium | Yes |
| 4 | The restore endpoint with its safety version, the operation id and every refusal | Large | Yes |
| 5 | Restore in the panel, the settings screen with the history policy, the docs | Medium | No |

PR 3 does not depend on 1 and 2 and can be built first or beside them. PR 4 needs 1 and 3. PR 5 needs 2 and 4.

## Done when

The B.5 card's "Done when", with the two narrowings above (the body of a rename version; unconfirmed formatting-only and deletion-only changes on an old client or a document opened later). Per PR:

1. A page edited through the real hub has a version within the worker's next rounds after it goes idle, and one while it is edited for longer than five minutes; two renames leave two versions; a full cap, a sidecar that is down and a page that cannot be read each leave the state the list endpoint reports; nothing is written with history off; live updates, the snapshot and the archive are as they were.
2. The panel lists and shows versions on desktop and phone, in both languages and themes, without changing the page that is open; a viewer sees it; every state has its message.
3. With the real hub and sidecar: text typed while a replacement is published is in the page or in an announced copy, for a current client and for one that ignores `Replaced`; an unconfirmed formatting-only or deletion-only change makes a copy on a current client; all clients converge; the import's replacement tests still pass.
4. A restore puts back content and title, keeps the state before it as a protected version that can itself be restored, and changes nothing when it is refused, repeated or cut short at any write.
5. An editor restores from the panel; an owner changes the policy on the settings screen; a non-owner cannot.

## Tests

The B.5 card's list holds. Where each part lands:

- **PR 1, server with real Postgres and the real sidecar:** capture of an empty, an edited, an imported and a file-note page; idle and five-minute eligibility with `MutableTimeProvider`; fairness and the bound per round; a capture racing an edit, a rename and a compaction (multi-batch, a late-committed row); the fingerprint skip; the policy read again at commit; the cap (eviction oldest first, protected versions kept, `full`); rename rows through a crash between the row and its version; backoff; versions unchanged after an archive purge; page and user deletion. The list's paging and cursor on equal times, another page's version id, every role, `EndpointPolicyTests`. A property test: generated edits, renames, compactions and captures against a full-replay oracle. `LogContentTests` gets a marker in a captured page.
- **PR 2, Vitest and Playwright:** the store and the panel (loading, paging, empty, pending, failed, off, error and retry, a reply that arrives after the page changed, focus in and back); the journey on the four devices; `a11y.spec.ts` for the panel's states in both themes. Playwright intercepts without a reload on the iPhone project (see `a11y.spec.ts`, the slow-load test).
- **PR 3:** `syncClient.test.ts` and `useKeepLostEdits.test.ts` for a Sync that arrives unasked, with and without `Replaced`, for text, formatting-only and deletion-only changes, confirmed and not; `convergence.test.ts` with a replacement in the middle; a client built to ignore type 8, as an old tab; server tests with real WebSocket clients: an update in flight stored after the replacement, the frames' order on the socket, a slow client's queue; the import's replacement tests unchanged and green; a document from IndexedDB opened after a replacement (the offline store's tests).
- **PR 4:** every refusal changes nothing (assert the rows of every table it writes); a failure injected at each write; a stored edit between the preview and the commit; a compaction during the restore; the page moved, renamed, trashed or the role lowered meanwhile; a pruned target; the operation id repeated, with the same and with another request, after the target was pruned; the safety version restored; search, backlinks, `file_text`; a Joplin re-import after a restore. The property test of PR 1 extended with restores and injected crashes.
- **PR 5:** the panel's restore (confirmation, one request at a time, `page_changed`, role and trash guards); the settings screen (validation, the warning, owner and non-owner); the journey with two clients, one offline; `a11y.spec.ts` for the confirmation and the settings.
- **While building, before a push and before the last push of each PR:** as `CLAUDE.md` says.

## Out of scope

- Everything the B.5 card lists: per-user attribution, a word-level diff, export of history, deleting single versions.
- Removing stored files no page uses (run 2.9b). When it is built, a file named by a version that can still be restored counts as used; that sentence goes into run 2.9b's row in this run's PR 4.
- Account settings on the settings screen (run B.9).
- New dependencies. None is needed as planned; ask first if one turns out to be.

## Questions

1. **A rename version's body is the body when the worker captures it, not at the instant of the rename.** Recommended: yes. Both titles are kept, which is what the architecture asks; the exact body at the rename would need a sidecar call inside the tree's lock.
2. **Formatting-only and deletion-only changes that had not reached the server are protected on a current client only**, not on a tab running the old client nor in an offline document opened later. Recommended: accept, and say so in the docs. No text is lost in these cases. The alternative is to refuse a restore while any old client has the page open, which a forgotten tab on a phone would block for as long as it stays open.
3. **The import's replacement changes with PR 3** (it also relays a Sync and `Replaced`). Recommended: yes; it closes a gap that exists today.
4. **Where the history policy lives in the interface:** a new `/settings` screen that run B.9 shares. Recommended: yes, rather than a dialog from the user menu.
5. **Capturing an existing library takes about two hours per 5,000 pages, in the background.** Recommended: accept. Faster means more sidecar time taken from people who are syncing.

## For the test pass

The B.5 card's four checks, unchanged. Added by this plan:

- After the upgrade that brings PR 1, leave the real library alone for an afternoon and check that pages get their first version, that typing stays smooth meanwhile, and that the app's log names no page as unreadable.
- Run a Joplin re-import of a changed note while typing in that note on the phone: the typed text is in the page or in an announced copy (PR 3).
