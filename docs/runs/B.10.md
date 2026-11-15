# Run B.10: A size limit on live edits

**Model:** O·high → S·med in the roadmap; kept. Planned on Opus 5.5 on 2026-11-15; builds on Sonnet 5.5 medium. Two PRs. Both touch the sync path, and PR 2 throws away a change on purpose, so both get `/code-review` at **high**.

**Status:** planned; PR 1 next. The maintainer answered the three questions on 2026-11-15, each as recommended: a page at the limit keeps a small margin to shrink, a refused change stays on the device until it is discarded, and `tooLarge` gets the same discard button.

## Goal

A page cannot grow, through live edits, past what the server can still send to a client that opens it:
- the sync hub refuses an update that would take a page's stored bytes over the limit, stores nothing and relays nothing, and says so with an error about that page;
- the editor says the change was not saved, stops sending for that page, and lets the person discard the change on this device to go on editing;
- a page at the limit can still be made smaller.

Finding 2.3 of the review of 2026-11-11: nine pastes of just under 2 MiB take a page past `Sync:MaxQueuedBytes` (16 MiB), and from then on nobody can open it. The reply overflows the queue and drops the connection, for every page on that socket.

## Inputs

- [`docs/architecture.md`](../architecture.md): the B.10 row; "Data model and sync design"; the attachments section's paragraph on what a page can be.
- [`docs/sync-protocol.md`](../sync-protocol.md): messages, "Errors about one page", "Limits".
- [`docs/design-notes/large-pages.md`](../design-notes/large-pages.md): the arithmetic behind `MaxPageBytes` (half the queue) and the import's three quarters.
- [`docs/review-2026-11-11.md`](../review-2026-11-11.md): finding 2.3.
- [`docs/runs/1.21.md`](1.21.md): how `MaxPageBytes` became one worked-out name. [`B.6.md`](B.6.md): the offline copy. [`B.5b.md`](B.5b.md): the restore's size rule.
- Code:
  - [`SyncHub.cs`](../../server/src/Inkpact.Api/Sync/SyncHub.cs): `HandleAsync` (an Update is stored and relayed under the room's lock), `SubscribeAsync` (loads the page under the lock), `PublishAsync` (a replacement, under the lock), `PageRoom`.
  - [`SyncOptions.cs`](../../server/src/Inkpact.Api/Sync/SyncOptions.cs): `MaxQueuedBytes`, `MaxPageBytes`, `MaxCopyBytes`. [`Frame.cs`](../../server/src/Inkpact.Api/Sync/Frame.cs): `SyncErrors`.
  - [`PageUpdateStore.cs`](../../server/src/Inkpact.Api/Sync/PageUpdateStore.cs): `LoadAsync`, `PageContent`.
  - [`HistoryRestore.cs`](../../server/src/Inkpact.Api/History/HistoryRestore.cs): `ContentMark.ReadAsync` (the page's stored bytes, in one statement) and the restore's size rule. [`ImportRunner.cs`](../../server/src/Inkpact.Api/Import/ImportRunner.cs): `MaxOldAndNewBytes`.
  - [`Compaction/`](../../server/src/Inkpact.Api/Compaction/): `CompactionOptions.LiveBytesThreshold` (2 MiB), and the sidecar's `compact` (`server/sidecar/src/ops.ts`), which rebuilds the document and drops deleted content.
  - [`syncClient.ts`](../../web/src/sync/syncClient.ts): `PageState`, `handleError`, `sendUpdate` (the `tooLarge` state), `onUpdate`, `sendSubscribe`. [`stores/sync.ts`](../../web/src/stores/sync.ts). [`offlineDocument.ts`](../../web/src/offline/offlineDocument.ts).
  - [`PageView.vue`](../../web/src/pages/PageView.vue) (the `tooLarge` and `blocked` notices), [`PageEditor.vue`](../../web/src/editor/PageEditor.vue) (read-only in those states).

## What is there now (found while planning)

1. **Nothing measures a page on a live edit.** `HandleAsync` appends every Update to the log and relays it. Only the import, the restore and the copy endpoint look at sizes.
2. **The sizes already in use:**
   - `MaxQueuedBytes`: 16 MiB, what may wait to go out to one client. A client over it is dropped.
   - `MaxPageBytes`: half of that, 8 MiB. The largest page state the import and the restore make.
   - Three quarters of the queue, 12 MiB: what a page's stored updates and a replacement may come to together, before the compaction that follows. The import calls it `MaxOldAndNewBytes`; the restore writes `sync.MaxQueuedBytes / 4 * 3`. The same rule under no shared name.
3. **What a client that opens a page is sent:** one Sync. Above `MaxDiffBytes` (2 MiB of log) or without the sidecar, it holds the snapshot and every live update as stored. Otherwise it holds a diff, which is never more than the page's state. So the page's **stored bytes** (snapshot plus live updates) bound what any client is sent. `ContentMark.ReadAsync` reads that figure in one statement.
4. **Stored bytes only grow under the room's lock.** Client updates are appended there, and so are the server's own replacements (`PublishAsync`, also for a page nobody has open). They shrink outside it: the compactor folds live updates into the snapshot under its own advisory lock. A page whose live updates reach 2 MiB is compacted at the next round (30 s). Compaction drops deleted content, so deleting text and compacting makes a page smaller.
5. **Updates build on each other.** A later update from the same client points at items in the earlier one. If the server refuses one update and stores the next, the log holds an update that refers to something it never got. Other clients keep it pending, and compaction would meet it. So once one update of a client is refused, nothing more of that client's page may be stored until it starts again from the server's state.
6. **The client has a precedent:** `tooLarge`, for one change bigger than a message (2 MiB). The page stops sending, stays unsent and is read-only, and the notice says "This change is too large to save. Reloading the page discards it."
7. **Probably no longer true, not confirmed:** since run B.6 the offline copy keeps a page with unconfirmed changes and sends them again after a reload (`OfflineDocument.adopt`). A refused change would then be refused again after every reload, and the page would stay stuck on that device. No test covers it. PR 2 starts by writing that test.
8. **Pages already over the limit** (made by live edits before this run) keep opening if they fit the queue. After this run they take no edit that grows them.

## Decisions

1. **The measure is the page's stored bytes**: snapshot plus live updates, what a client in log mode is sent and a bound on what any client is sent (item 3).
2. **One name for the limit:** `SyncOptions.MaxStoredBytes`, worked out as `MaxQueuedBytes / 4 * 3` (12 MiB), as run 1.21 did with `MaxPageBytes`.
   - The import's `MaxOldAndNewBytes` and the restore's `MaxQueuedBytes / 4 * 3` use it.
   - It is the room the queue keeps for a page whose log has not been compacted yet: the reply plus the traffic around it.
3. **The rule** (question 1):
   - An update is refused when the page's stored bytes plus the update would be over `MaxStoredBytes`.
   - **Except a small one:** an update of at most `MaxQueuedBytes / 256` (64 KiB, `SmallUpdateBytes`) is still taken up to `MaxQueuedBytes / 16 * 13` (13 MiB, `MaxStoredBytesForSmallUpdates`). A person at the limit can then delete text (a deletion is a few bytes), and the next compaction makes the page smaller. A paste or a long burst of typing cannot use that margin.
   - All three figures are worked out from the queue, not configured, so a larger queue moves them together.
4. **Counting without a query per keystroke:**
   - The room keeps `StoredBytes`. It is known after a subscribe (the load the subscribe does anyway), grows by every update stored, and is unknown after a `PublishAsync` (the replacement read the log itself).
   - Only when the kept figure plus the update would cross the limit, or the figure is unknown, does the hub read the real one (`ContentMark.ReadAsync`, under the room's lock). A compaction may have made it smaller.
   - It reads at most once every 10 seconds per page. Between reads the kept figure stands; it can only be too high, never too low (item 4).
5. **A refusal:**
   - The Error `page_too_large` about that page. Nothing is stored or relayed.
   - **The subscription stops taking updates** (item 5): every later Update on it is dropped unstored, without another Error, until the client unsubscribes and subscribes again.
   - **A log line** with the page id and the two sizes, as numbers.
6. **The protocol:** `page_too_large` joins "Errors about one page" in [`docs/sync-protocol.md`](../sync-protocol.md), with the rule and the margin. "Limits" names `MaxStoredBytes`. A client from before this run gets an error code it does not know. It keeps sending, the server drops what it sends, and its page looks synced while it is not. That tab is fixed by a reload once the client is new.
7. **The client, a new page state `full`:**
   - From the Error `page_too_large`. The page stops sending, stays unsent, does not subscribe again on a reconnect, and the editor is read-only, as with `tooLarge`.
   - **The notice:** "This page has reached its size limit, so your last change was not saved. You can still select and copy from it. Discard the change to go on editing; large content fits better on a page of its own or as an attachment."
8. **Discarding the change** (question 2):
   - A "Discard unsaved change" button in the notice, behind a confirmation in place: "Discard the change that was not saved? It is not on the server, and it will be gone from this device."
   - It closes the page's handle, removes this device's offline copy of that page for this account, and opens the page again from the server. Recovery copies of lost edits (`useKeepLostEdits`) for other pages are left alone.
   - **The same button in the `tooLarge` notice** (question 3), whose text loses "Reloading the page discards it" if item 7 is confirmed.
9. **No warning before the limit in this run.** The existing notice for a page over 5 MiB ("may be slow") stays. Its text adds that a page cannot grow past about 12 MB.
10. **No new dependency, no migration.**

## The PRs

| PR | What | Size | `/code-review` |
| --- | --- | --- | --- |
| 1 | The server: `MaxStoredBytes` and its users, the room's count, the refusal and the closed subscription, the protocol page | Medium | **high** |
| 2 | The client: the `full` state, the notice, discarding the change (also for `tooLarge`), both languages, e2e | Medium | **high** |

PR 1 can merge alone: a client from before PR 2 gets an error it does not know (decision 6), which is no worse than a page nobody can open.

## Done when

- **The limit holds:** no live edit takes a page's stored bytes over `MaxStoredBytes`, except small updates within the margin (decision 3). A page made by live edits always opens.
- **A refusal is clean:** the refused update and everything after it on that subscription is neither stored nor relayed, and the other people on the page see nothing of it.
- **A page at the limit can shrink:** deleting text is taken, and after the next compaction the page takes edits again.
- **The person knows:** the editor says the change was not saved, stays readable, and offers to discard the change. After discarding, the page is the server's and can be edited. A reload never sends the refused change again.
- **One rule:** the import, the restore and the hub use `MaxStoredBytes`.

## Tests

- **Server, real Postgres.** A class of its own (`SyncPageLimitTests`), with a small queue so a test needs kilobytes, not megabytes. For example `Sync:MaxQueuedBytes` at 256 KiB, which gives a limit of 192 KiB, small updates of 1 KiB, and a margin up to 208 KiB. The import's note limits must be set at or below half of it, or start-up refuses.
  - Under the limit: stored and relayed.
  - **An update that would cross it:** `page_too_large` to the sender, nothing stored, nothing relayed to a second subscriber.
  - **After a refusal,** a small update on the same subscription: not stored, no second Error. After unsubscribe and subscribe: taken again.
  - **The margin:** a small update over the limit is taken; a large one is not; a small one over the margin is not.
  - **The count:** after a compaction makes the page smaller, an update that now fits is taken (the kept figure is read again). After `PublishAsync` the figure is read. Two subscribers' updates both count. A second connection is not affected by the first one's refusal until its own update crosses.
  - `read_only` still comes first for a viewer.
  - **The shared rule:** the import and restore tests that check three quarters of the queue pass unchanged.
  - **`SyncOptions`:** the three worked-out figures for the default and a configured queue.
- **Web, Vitest:**
  - `syncClient`: `page_too_large` gives `full`; nothing more is sent; no subscribe on a reconnect; the page stays unsent.
  - The offline copy: a `full` or `tooLarge` page, reloaded, does not send the refused change again once it was discarded. The test of item 7 comes first: it shows what a reload does today.
  - Discarding: the handle and the page's offline copy go, other pages' copies and recovery copies stay, and the page opens from the server.
  - `PageView`: the notice, the confirmation, focus, both languages.
- **Playwright:**
  - `page-limit.spec.ts`: `page.routeWebSocket` passes the socket through and answers one Update with `page_too_large`. The notice shows, the editor is read-only, discarding asks first and then the page is editable with the server's content. Desktop and phone.
  - `a11y.spec.ts`: the notice and its confirmation.
- **While building:** the server test classes you change, `vitest related`, the new spec. Before each push `pnpm check:push`; before the last push of each PR, `pnpm check` once with Postgres up.

## Docs to update

- [`docs/architecture.md`](../architecture.md), in PR 1: the B.10 row (three quarters of the queue, not the whole queue), and the paragraph on what a page can be.
- [`docs/sync-protocol.md`](../sync-protocol.md), in PR 1: the error, the rule, the closed subscription, "Limits".
- [`docs/design-notes/large-pages.md`](../design-notes/large-pages.md), in PR 1: live edits next to the import and the restore.
- [`docs/self-hosting.md`](../self-hosting.md), in PR 1: "Very long notes" says live edits stop at three quarters of `Sync__MaxQueuedBytes`.
- [`docs/test-pass.md`](../test-pass.md): the checks below, under "B.10".
- This card: a handoff per PR.

## Out of scope

- **A gauge of how full a page is**, or a warning before the limit other than the existing notice (decision 9).
- **Keeping the refused text as a copy.** The offline copy makes a copy of the whole page, and a page at the limit is too large for one (`MaxCopyBytes`). The person can copy what they need while the page is read-only.
- **Measuring the page's state** (decoding updates on the hub). The hub stays opaque; the stored bytes are an upper bound.
- **Fixing pages already over the limit** (item 8): restoring an earlier version is the way back.
- **Splitting a large page, or moving content to an attachment for the person.**

## Questions

1. **Is a page at the limit still allowed to shrink?**
   - **Recommended:** yes, through the margin of decision 3. Updates of at most 64 KiB are taken up to 1 MiB over the limit (by default), so deleting text works and the next compaction makes the page smaller. 13 MiB still fits the 16 MiB queue with the traffic around it.
   - **The other way:** a hard limit with no margin. Simpler, but a page at the limit can only be made smaller by restoring an earlier version.
2. **What happens to a refused change on the device?**
   - **Recommended:** it stays in that tab and on that device, unsent, with the page read-only, until the person presses "Discard unsaved change". They can copy text out first.
   - **The other way:** discard it at once, as soon as the server refuses it. Nothing is ever stuck, but the person has no moment to copy what they typed.
3. **Does a change that is too large for one message (`tooLarge`) get the same discard button?**
   - **Recommended:** yes. It has the same problem (item 7, if confirmed), and one button for both is less to learn and to test.
   - **The other way:** leave `tooLarge` as it is, and fix it in a bug-fix run if item 7 is confirmed.

## For the test pass

- **The limit, on the laptop:** paste a large text (a few MB) into one page again and again. Before it passes about 12 MB, the editor says the change was not saved. Copy a line from the page, then discard the change: the page is editable again, as it was before the last paste.
- **Shrinking:** on a page at the limit, delete a large part. Within a minute the page takes a paste again.
- **The phone:** open that page on the phone while the laptop is refused. It opens, and shows nothing of the refused paste.

## Handoff
