# Run B.5b: The rest of version history: capture, the history panel, restore and the owner's setting

**Model:** O·high → S·high in the roadmap. Planned on Fable 5.2 on 2026-11-12 from a reading of #122's code against the B.5 card. Sync, restore and the client's lost-edit check are critical paths: `/code-review` at high on PRs 3 and 4 before they merge, at medium on the others.

**Status:** In progress. The five questions were answered on 2026-11-12, each as recommended. Built in the order PR 3, 1, 2, 4, 5 (PR 3 first: it closes a gap that exists today). PRs 3 (#144), 1 (#145) and 2 (#148) are merged; PR 4 (the restore endpoint) is described under **Handoff**; PR 5 is not started. History can be captured, looked at and restored through the API; the panel gets its Restore button in PR 5.

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

All five were answered "yes, as recommended" by the maintainer on 2026-11-12.

1. **A rename version's body is the body when the worker captures it, not at the instant of the rename.** Recommended: yes. Both titles are kept, which is what the architecture asks; the exact body at the rename would need a sidecar call inside the tree's lock.
2. **Formatting-only and deletion-only changes that had not reached the server are protected on a current client only**, not on a tab running the old client nor in an offline document opened later. Recommended: accept, and say so in the docs. No text is lost in these cases. The alternative is to refuse a restore while any old client has the page open, which a forgotten tab on a phone would block for as long as it stays open.
3. **The import's replacement changes with PR 3** (it also relays a Sync and `Replaced`). Recommended: yes; it closes a gap that exists today.
4. **Where the history policy lives in the interface:** a new `/settings` screen that run B.9 shares. Recommended: yes, rather than a dialog from the user menu.
5. **Capturing an existing library takes about two hours per 5,000 pages, in the background.** Recommended: accept. Faster means more sidecar time taken from people who are syncing.

## Handoff

### PR 1: capture, and the list and preview endpoints

- **Schema** (`AddHistoryCapture`): `page_history_state`, `page_title_changes`, and `fingerprint` and `content_seq` on `page_versions`. No version existed before, so nothing is filled in.
- **Capture:** [`HistoryCapture.cs`](../../server/src/Inkpact.Api/History/HistoryCapture.cs) and its worker, every 30 seconds, at most 20 pages a round, one at a time, oldest change first. A page is due when it has something that is in no version and is idle for two minutes, or its oldest such change is five minutes old (looked for in the archive too, since the compactor moves the updates of a page that is typed in without a pause there), or a title waits. The version is written under the workspace's history lock with the policy read again: off writes nothing (and drops the titles that waited), an unchanged page (fingerprint, title and file text) only moves the cursor, a cap makes room from the oldest unprotected versions or records `full` and removes nothing.
- **Renames:** `TreeService.RenameAsync` adds a `page_title_changes` row in its own transaction when the title really changes, and so does a Joplin re-import that takes a note's new title (`ImportRunner.WriteFieldsAsync`). Neither asks whether history is on: every round of the worker first drops the rows of workspaces where it is off, so that rule is in one place and titles that waited when history was turned off do not come back as versions later. The capture turns each row into a version of kind `Rename` with who renamed and deletes the row in the version's transaction. A rename that commits while a capture is under way waits for the next one.
- **Reading:** `GET /api/pages/{pageId}/versions` (newest first, 50 by default and 100 at most, a cursor of time and id, the page's history state) and `GET /api/pages/{pageId}/versions/{versionId}` (title and the editor's document; `404 version_not_found`, `409 version_unreadable`, `503 history_unavailable`). Viewer role on the page; a page in the trash can still be looked at. One version's content is read at a time (`HistoryPreviewGate`): each is a replay in the sidecar, and a request that waits longer than `History:PreviewWait` (10 seconds) gets `503 history_unavailable`. The answer is `no-store`, as every answer of the API, so **the panel of PR 2 must keep what it has shown** instead of asking again on every selection.
- **Retention is on by default** (`History:RetentionEnabled`), from this PR.
- **Settings:** `History:CaptureEnabled`, `CaptureInterval`, `CapturesPerRound`, `CaptureIdleAfter`, `CaptureAtLatestAfter`, `CaptureMaxLiveBytes`, `CaptureLargeLogAfter`, `PreviewWait`; checked at start-up.

**Where it differs from the plan above, and why:**

1. **The cursor also counts updates** (`captured_updates`: those in the snapshot plus the live ones), and that count decides whether a page has something to capture. The plan's sequence number alone misses an update that commits late with a number below one already captured: it would stay out of every version until the page's next edit. `captured_seq` is still kept, and still decides which change is the oldest that is in no version.
2. **A sidecar that is down does leave a mark on the one page that met it:** no failure code, so the list does not call it failed, but that page waits a minute, doubling to an hour at most. Without it a page that takes the sidecar down every time (a timeout kills the sidecar) would stay first in line and no other page would ever be captured. The round ends, as planned. The same wait applies to a page whose capture fails for a reason nobody foresaw; there the round goes on to the next page. These waits are counted apart (`waiting_attempts`) and leave what is known of the page as it is: a page that could not be read is still reported as that, and does not wait longer for it.
3. **Its own limit instead of `Sync:MaxDiffBytes`** for "leave a long log to the compactor first": `History:CaptureMaxLiveBytes`, with the same default of 2 MiB. Changing the one does not change the other. And only for an hour (`History:CaptureLargeLogAfter`): after it the page is captured from its long log, because the compactor may be off or may not get that page compacted.
6. **A failure does not always wait out its time.** A page that could not be read is tried again once it was written to after the failure (at most every five minutes); a page that was `full` is tried again as soon as the owner saves the policy.
7. **Under a cap, titles that waited can be left out.** When the versions of several renames and the page as it is do not fit together, the earliest titles go, as the oldest versions do under a cap. Each is a full copy of the page, so a few renames of a large page would otherwise never fit and the page would stay `full`.
4. **A version is never earlier than the page's newest version:** when its time would be, it gets the newest one's time plus a microsecond. A rename that waited for the capture after it would otherwise sort before that capture's version, and the page's newest version would carry a title the page no longer has.
5. **The list's page size** is 50 by default, as planned, and can be asked up to 100.

**Measured** (this laptop, real Postgres and sidecar): a capture of a page of 8.0 MB (7,600 paragraphs) takes 0.8 s from one live update and 0.4 s from its snapshot plus three live updates, so the sidecar is taken for under a second. The worker's query for due pages, every 30 seconds, looks only at pages that can have something (a live update, a waiting title, a snapshot with more updates than were captured): on 10,000 pages it takes about 20 ms when nothing changed, 0.1 to 0.3 s with 50 pages being typed in, and 0.1 to 0.4 s while none is captured yet (the first hours after the upgrade).

**Not in this PR:** nothing shows history in the interface (PR 2), and the per-page property test is a seeded schedule of edits, compactions in small batches, renames and captures against a full replay, not an FsCheck property; PR 4 extends it with restores and injected crashes and is the place to move it to FsCheck.

**Review:** `/code-review` on 2026-11-12 gave ten findings; all are fixed in this PR (the five-minute rule against the compactor, several renames under a cap, a title a re-import takes, titles that waited when history was turned off, one failure wiping another, a long log nobody compacts, failures that outlast their cause, the cost of the due query, the rename's extra query, and previews taking the sidecar). One part of a fix was taken back: letting the browser keep a version's content (an `ETag`) would go against "answers of the API are never cached".

**Checks:** see the pull request.

### PR 2: the history panel, read-only

- **Where:** a History button (a clock) in the page's header, beside the star, for every member and also for a page in the trash; not shown while the tab is offline, since the history is read from the server. It opens [`HistoryPanel.vue`](../../web/src/history/HistoryPanel.vue), whose code is fetched the first time it is asked for ([`useHistoryPanel.ts`](../../web/src/history/useHistoryPanel.ts)): the panel opens once the code is there, and a failed fetch (a new version of the app was installed, the network dropped) is said in a notice and the button works again. The panel closes with another page, and with anything else that takes the page away (not found, failed to load), so it never comes back by itself.
- **The panel:** a Reka dialog, full screen on a phone and a sheet at the side from `md`. The list on one side (newest first, when, what kind, who for a rename, the title then; "Show older versions" for the next 50), the chosen version on the other. On a phone they take turns, with "Back to the versions" and the focus moved to the version's heading and back to its row. Closing gives the focus back to the History button, also on an iPhone, where a tap does not focus a button.
- **States:** loading, could not be loaded (with Try again), no versions (also with history off), and a notice for `pending`, `failed` (said at once), `full` and `off`. A version that cannot be shown says why: the sidecar is busy (Try again), the version is gone, it holds something this app cannot display, or it could not be loaded.
- **The version's content:** [`VersionPreview.vue`](../../web/src/history/VersionPreview.vue), a second TipTap editor, read-only, from the same `createExtensions` without a Yjs document (the `doc` option is now optional and leaves Collaboration out). The labels, the files and the marks on links to pages that are gone come from [`contentOptions.ts`](../../web/src/editor/contentOptions.ts), which the page's editor uses too. So the same schema and the same link rule when rendered (`isAllowedLink`), pictures and files by id from the app's own address, and links to pages in the trash struck through, also when that changes while the version is shown. A click on a link is taken and does nothing. The preview is made again when the language changes, as its words are set when it is made.
- **What the panel keeps:** [`useVersionHistory.ts`](../../web/src/history/useVersionHistory.ts) keeps each version's content once read, for as long as the panel is open, as PR 1's handoff asked (answers of the API are `no-store`). The panel is made for one page (another page closes it), so the composable takes the page's id as a plain value. The list is read once when the panel opens; it does not refresh itself while open.
- **e2e:** [`history.spec.ts`](../../web/e2e/history.spec.ts) is the journey on the four devices and [`a11y.spec.ts`](../../web/e2e/a11y.spec.ts) has the panel's states, both with the versions answered by the test (`page.route`), not made by the server. A capture as fast as compaction's in the e2e server (3 idle seconds) was tried first: the rest of the suite then timed out, one to four tests a run in three runs, against none in a run without it. A capture replays a page three times in the sidecar, which takes one request at a time; that is my reading of why, not something the log showed. So the e2e server keeps the capture's own pace, and the real capture-to-panel path is covered by the server's worker test (`HistoryEndpointTests`, through the hub). PR 4's restore makes versions at once, and should bring a journey with real versions.
- **Other e2e tests made sturdier**, since the suite's shared workspace grows with every spec: the sidebar size check in `tree-ui.spec.ts` measures in one pass instead of one round trip per control, `openSidebar` waits up to 15 seconds for the drawer, and the import test that waits for another device's import gets the 150 seconds its own retry asks for (its 30-second default cut the 120-second retry short).

**Where it differs from the plan above, and why:**

1. **A composable, not a store:** the panel's state lives as long as the panel and belongs to nothing else, so it is `useVersionHistory`, not a Pinia store.
2. **Nothing is chosen when the panel opens:** each version shown is a replay in the sidecar (PR 1, finding 10), so none is read until the person picks one.
3. **A new page's first version is a rename:** a page made through "New page" is named by a rename, which the capture turns into a version of kind `Rename` with the page as it was then (empty, or with what was typed meanwhile). That is what PR 1 builds; the panel shows it as it is. Worth a look in PR 5, when restore makes the list something people act on.
4. **The header's buttons keep their size, and the status pill gives way:** with a third item in the header, the star and the clock shrank below 44 pixels on a 320-pixel screen. They now keep their size (`shrink-0`, also on the star), and the sync status shortens its words with an ellipsis when there is no room; a screen reader still hears all of it. Letting the row wrap instead was tried and dropped: the pill then moved to a line of its own whenever its text grew ("saving on this device"), and the page jumped while someone typed, which an iPhone test caught.

**Review:** `/code-review` on 2026-11-13 gave nine findings, all fixed in this PR: a link clicked in the preview was followed by the browser; a panel chunk that failed to load left the button dead; the panel came back by itself after the page was away; the preview's link marks and words did not follow the tree and the language; a version id went into a selector unescaped; the page editor's link, label and file options were copied into the preview (now `contentOptions.ts`); the composable handled a change of page that never happens, and a list read again could leave a version loading for good; and the editor was destroyed twice.

**Not in this PR:** restore (PRs 4 and 5). A viewer's view of the panel is not tested end to end, as the e2e suite has one account; the button has no role check and the endpoints' roles are tested on the server.

**Checks:** see the pull request.

### PR 4: the restore endpoint

- **`POST /api/pages/{pageId}/versions/{versionId}/restore`** with `{operationId, expectedContent, expectedVersion}`, editor role, CSRF. It answers `{beforeVersionId, restoreVersionId, title, version}`. [`HistoryRestore.cs`](../../server/src/Inkpact.Api/History/HistoryRestore.cs).
- **Before the locks:** first the cheap look, the content mark and metadata version against the request (`page_changed` without any replay). Then, one at a time with the previews (`HistoryPreviewGate`, else `503 history_unavailable`): the version is read back whole (else `version_unreadable`), its files must still be in the workspace (`attachment_missing`; a file copied into another page keeps its first page's row), and the page is observed as one snapshot by the capture's builder (`page_unreadable` when that cannot be read). The replacement is built against that snapshot's state, and the page after it is compacted and checked against the replacement's fingerprint. `413 content_too_large` when the replacement or the page after it is over `Sync:MaxPageBytes`, or the stored page and the replacement together are over three quarters of `Sync:MaxQueuedBytes` (the import's rule). A restore whose page is then more than `Sync:MaxDiffBytes` as stored is compacted at once.
- **Under the locks:** the hub's page lock (`PublishAsync`), then one transaction with the tree lock, the history lock and the person's membership. Inside, everything is checked again: the operation id, the version, the page live (`page_in_trash`), history on (`history_off`), the role (`403` or `404`), and the page exactly as observed (`page_changed`). Then, in order: titles that waited become Rename versions (each skipped when it shows the same as the version before it); the page as it was becomes the safety version, protected 24 hours: a copy of kind `BeforeRestore`, or, when the page's newest version shows the page as it is already, that version itself, protected; the replacement update is stored; the Restore version, the title (a new metadata version when it changes), the search text, `file_text`, the backlinks, the capture's cursor and the operation's row are written. The hub then relays the replacement as Replaced and Sync.
- **The list** gives the page's content mark (`pageContent`) for the client to send back.
- **Not touched:** the page's place, tags, favorites, to-do fields, and the Joplin import's comparison base.

**Where it differs from the plan above, and why:**

1. **A content mark instead of `expectedSeq`:** `"<last sequence number>.<updates stored>"`, from the list. The sequence number alone misses an update that commits late with a lower number, the same gap PR 1 closed for the capture.
2. **The replacement is built against the page's full state** from the capture's builder (one observation, verified against a replay), not against the stored inputs. The commit then checks the stored page is still exactly that observation.
3. **The role is read again at the commit,** under the locks, as the B.5 card asks; the endpoint's filter alone saw it before the slow part. A role lowered meanwhile writes nothing.
4. **Titles that waited for the capture become versions first,** as the capture would have made them, so a restore never loses a title from the history.
5. **The cap must hold the safety version and the restore version,** not the safety version alone: both are written. Titles that waited are left out first, oldest first, as in the capture.
6. **The capture's cursor is moved to the restore,** so the capture does not make an automatic version that equals the Restore version.
7. **Every restore raises the tree version** (the tree lock is the workspace row's update), also when the title stays: clients read the tree again once. Harmless, and the lock is needed.
8. **New error codes** beyond the plan's: `page_unreadable` (the page as it is cannot be read back, so no safety version can be made), `restore_invalid` (no operation id, or a mark that does not read), and `forbidden`/`not_found` at the commit.

**Review:** `/code-review` (high) on 2026-11-13 gave ten findings, all fixed in this PR: a capture that observed the page before a restore no longer writes after it (`Superseded`: it checks the cursor under the history lock); a version with a file copied from another page of the workspace restores; the import's size rule and compaction at once; the cheap look before any replay, and the replays one at a time; the content mark read in one statement, which two queries could get wrong across a compaction; an operation id taken meanwhile under another history lock answers `operation_mismatch` instead of a 500; no copies of the newest version (titles that waited, or the safety version); the list reads the mark once; one formula for a version's bytes (`PageVersion.PayloadOf`); and the title through the tree's rule (`TreeService.RetitleUnderLock`).

**Tests:** [`HistoryRestoreTests.cs`](../../server/tests/Inkpact.Api.Tests/History/HistoryRestoreTests.cs) (32, real Postgres, sidecar and hub): what a restore writes and keeps; the safety version restored; links, search and file text; titles that waited; the Replaced and Sync frames a subscriber gets; every refusal comparing the rows of every table it could write; a crash at each of five stages, with nothing written and nothing relayed; an edit stored and a compaction run between the preview and the commit; a role lowered meanwhile; the operation id repeated, after a prune, and reused; viewer, stranger, signed out and no CSRF. [`HistoryRestorePropertyTests.cs`](../../server/tests/Inkpact.Api.Tests/History/HistoryRestorePropertyTests.cs) is FsCheck with generated edits, compactions, captures, renames, restores, stale restores and crashes against a full replay, with floors on how often each happened; this is the move to FsCheck that PR 1's handoff promised.

**Test Postgres:** the suite's container now gets 512 MB of `/dev/shm` instead of Docker's 64 MB. With this PR's tests, parallel queries and index builds in six classes at once ran out of it ("could not resize shared memory segment", 2 to 23 failures a run; none on main). The production compose file also leaves Postgres at 64 MB. One server's load is far below the test suite's, so nothing is changed there; worth knowing if that error ever shows in a log.

**Not in this PR:**
- The Restore button and its e2e journey with real versions: both come with PR 5, which brings the restore to the interface. (PR 2's handoff said PR 4; PR 4 is server only.)
- A Joplin re-import after a restore. The restore does not write `import_items`, so the import sees the page as edited here; there is no end-to-end test of that yet.

**Checks:** see the pull request.

## For the test pass

The B.5 card's four checks, unchanged. Added by this plan:

- After the upgrade that brings PR 1, leave the real library alone for an afternoon and check that pages get their first version, that typing stays smooth meanwhile, and that the app's log names no page as unreadable.
- Run a Joplin re-import of a changed note while typing in that note on the phone: the typed text is in the page or in an announced copy (PR 3).
