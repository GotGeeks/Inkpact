# Run B.1: Links between notes, backlinks, broken links

**Model:** S·high in the roadmap. Planned on Opus 5.5 high; builds on Sonnet 5.5 high. It touches the compactor's transaction and the sidecar's contract, so `/code-review` at high on Opus before each PR merges.

## Goal

A person can link one page to another from the editor, sees on every page which pages link to it, and a link to a page that is in the trash or gone stays where it is, marked as broken, and works again when the page is restored. The links the Joplin import already made (run 1.16c) count from the start: the backlinks of an imported library are there without editing anything.

## Inputs

- `docs/architecture.md`: the B.1 row, "Compaction guarantees", the sync flow (updates are opaque, decoded only in the sidecar), "Links between notes" under "Importing again" (page links are `/p/<uuid>` link marks), "Permission resolution", and `live_pages`.
- `docs/design/inkpact_slate/DESIGN.md`: "backlink pills" in the reading column, the desktop's "on-demand slide-out metadata/backlinks drawer (320px)", `secondary` for link highlights.
- `docs/test-pass.md`: manual checks go there, not into this run.
- Code:
  - `web/src/editor/links.ts` (`pageIdOfLink`, `isAllowedLink`), `LinkMenu.vue`, `linkOpening.ts`, `extensions.ts` (TipTap Link: autolink, link on paste), `slashExtension.ts` and `blockCommands.ts`, `stores/tree.ts` (`byId`, the live pages; `trash`);
  - `server/sidecar/src/ops.ts` (`OP_COMPACT`) and `text.ts` (the walk for the search text);
  - `server/src/Inkpact.Api/Compaction/Compactor.cs`, `Crdt/IYjsCodec.cs` (`YjsCompaction`) and the sidecar client;
  - `Permissions/PermissionService.cs`; `Pages/TreeService.cs` (`live_pages`).

## What is there now (found while planning)

1. **No way to make a link in the editor.** Links come from typing or pasting a web address (autolink, link on paste), from pasting a copied page address, and from the import. `LinkMenu` only opens a link. So "links between notes" needs a way to make one, not only backlinks.
2. **Page links already exist in content:** every link the import made is a `link` mark with `href="/p/<uuid>"`. A page address pasted from the address bar is stored in full (`https://host/p/<uuid>`); `pageIdOfLink` reads both.
3. **The server knows nothing about links.** Content is opaque Yjs, decoded only in the sidecar. The compactor already gets the page's plain text from the sidecar (`OP_COMPACT`) and writes `content_text` for search in its transaction. Link targets are not in that text (`text.ts` skips attributes).
4. **Compaction runs** after 500 updates, 2 MiB of updates (run 1.22), or two idle minutes, which is also how soon a new word can be found by search. A compacted page that gets no new updates is never looked at again, so the pages that exist today would never get their links indexed by compaction alone.
5. **The client knows the live pages of the workspace** (`tree.byId`) and the trash (`tree.trash`). That is enough to tell whether a link's page is live, in the trash, or unknown here.
6. **The design** puts backlinks in a desktop drawer that opens on demand, next to metadata and version history (B.5), and mentions "backlink pills" in the text column.

## Decisions

1. **Two PRs** (question 1):
   - **B.1a:** the link index, backlinks and broken links. It works at once on the links the import made.
   - **B.1b:** making links in the editor.
2. **The link index** (B.1a):
   - **Table `page_links (source_page_id, target_page_id)`:** primary key both, an index on the target, cascade on the source page, no foreign key on the target (a link may point to a page that does not exist yet, as the import's do, or no longer exists).
   - **Written only by the compactor**, in its own transaction: the source's rows are replaced by what the sidecar found. Every guarantee stays: the rows commit with the snapshot or not at all, and the verification is unchanged.
   - **The sidecar's `OP_COMPACT` answer gets one more part:** the distinct page ids of `link` marks whose `href` is exactly `/p/<uuid>` or an absolute `http(s)` address whose path is exactly that (any host: the server does not know its public name; a link to another Inkpact's page whose id is not a page here simply finds nothing). *(Changed by the review: `/p/<uuid>` only, no full addresses; see Review fixes 4.)*
   - **Contract:** the sidecar's contract tests and the C# codec's tests cover the new part; `YjsCompaction` gets `Links`. `OP_READ`, `OP_BUILD` and `OP_REPLACE` do not change.
3. **Indexing what exists** (B.1a):
   - **`pages.links_indexed`** is a small integer: the version of the extraction that last indexed the page. A migration adds it at 0, and the compactor sets it to the current version (1).
   - **A one-off backfill:** a hosted job at start-up indexes pages below the current version, in batches, without compacting them. It reads the inputs, asks the sidecar's `OP_COMPACT` for the links only, and writes the rows and the version in a transaction under the compactor's advisory lock for the page (a try: a page being compacted is left to the compactor, which indexes it anyway). *(Changed by the review: only pages without live updates, their snapshot through a links-only operation; see Review fixes 1 to 3.)*
   - **Pace:** it stops when the sidecar is down and goes on later; a library of 5,000 notes takes about as long as reading it once.
   - **Next time:** a later change to the extraction raises the version, and the same job runs again.
4. **How fresh the backlinks are:** as fresh as search, since both come from compaction. A link typed now shows on the other page after the source page's next compaction, within about two minutes of the last edit. The panel says nothing about it; the architecture does.
5. **The backlinks endpoint:** `GET /api/pages/{id}/backlinks` with `RequirePageRole(Viewer)`.
   - It returns the source pages that are live (not in the trash or under it, through `live_pages`) and that the person may view (the permission service, per source workspace). *(Changed by the review: each source through the page role; see Review fixes 5.)*
   - Each comes with its id, title and the title of its parent, sorted by title.
   - The page itself is left out.
   - It answers with a list, at most 200 entries and a `more` flag.
6. **The backlinks panel** (question 3):
   - **Where:** a section below the page's content, "Linked from" with the count.
   - **What:** a list of the source pages (their title, and the parent's title in small type), each opening the page.
   - **When:** it loads when the page opens and again when the page's tab gets focus.
   - **How:** on all screen sizes, with keyboard and screen-reader names. Nothing when there are none.
   - **Later:** the drawer of the design can come with version history (B.5), which needs one.
7. **Broken links** (B.1a), in the editor and on a page that cannot be edited.
   - **What counts:** a decoration (a ProseMirror plugin, no change to the document) marks a page link whose page is not live: in the trash, or unknown here.
   - **How it looks:** it gets a class (`line-through`, `text-on-surface-variant`, tokens from `DESIGN.md`) and an `aria-description` / `title` from i18n ("This page is in the trash" or "This page does not exist here").
   - **Clicking it** opens the page's route as now, which already shows the trash or "not found" message.
   - **When it updates:** the decoration follows the tree store, so restoring the page makes the link whole at once, and nothing is ever written to the content.
8. **Making a link** (B.1b, question 2):
   - **Typing `[[`** opens a page picker at the cursor, a Reka UI combobox over the workspace's live pages, filtered by title as you type, with the path shown for pages with the same title. Choosing a page inserts its title as text with a link mark `href="/p/<id>"`. Escape keeps what was typed.
   - **The slash menu** gets "Link to page", which opens the same picker.
   - **With text selected,** `Ctrl+K` and a link button in the toolbar (the phone toolbar too) open the picker on the selection. It also takes a web address, so a selection can become a link to anywhere; the address goes through `isAllowedLink`. In the link menu, "Remove link" is added.
   - **Pasted page addresses:** a page address of this origin, pasted or typed, is stored as `/p/<id>`, so the index finds it and the link survives a change of host name.

## Done when

- The backlinks of every imported page are there after the backfill, and a new link shows on its target within two minutes of the last edit.
- A page in the trash, or under a page in the trash, is not listed as a backlink; one in another workspace the person cannot see is not either.
- A link to a page in the trash is struck through with its reason, and is whole again right after the page is restored. The content does not change.
- `[[`, the slash menu, `Ctrl+K` and the toolbar make page links, on desktop and phone. A pasted page address is stored as `/p/<id>`.
- Everything in both languages, CSP clean, keyboard and screen reader names checked by `a11y.spec.ts`.

## Tests

- **Sidecar (Vitest):** the link targets of a page:
  - relative and absolute `/p/<uuid>`;
  - not other paths, not with query or fragment;
  - duplicates once;
  - links in tables, lists and quotes;
  - a deleted link gone;
  - plus the contract's frame tests.
- **Server:**
  - the compactor writes and replaces `page_links` in its transaction, and an injected crash at every stage leaves the old rows or the new ones (the existing crash hooks);
  - the backfill: version, skip under the lock, the sidecar down, idempotence, batches;
  - the endpoint: live sources only, trash and under trash left out, permission per source workspace, the 200 cap, the page itself left out;
  - the deny-by-default policy test covers the new endpoint;
  - property test: after random link edits and compactions, `page_links` equals the links of a full replay (FsCheck, as the compaction tests).
- **Vitest:**
  - the broken-link decoration (live, trash, unknown, restored);
  - the backlinks panel (empty, list, error, focus reload);
  - the picker (filter, same titles, Escape, insert);
  - `Ctrl+K` with and without a selection, the web address rule, remove link;
  - paste normalisation.
- **Playwright:**
  - link two pages with `[[`, wait for the compaction (the test sets the idle time short through the e2e server's settings), and see the backlink;
  - trash the target, see the link marked broken, restore it;
  - the same on the phone with the toolbar's link button;
  - CSP clean on every project.

## Docs to update

- `docs/architecture.md`:
  - "Links between notes": the index, the backfill, freshness, broken links;
  - the compaction section: the links in its transaction;
  - the data model: `page_links`, `pages.links_indexed`.
- `docs/test-pass.md`: the checks under "For the test pass".

## Out of scope

- **Page titles in links that follow renames:** a link keeps the text it was made with, as in Joplin and Markdown. Mention-style links that show the current title would be a node type of their own.
- **Links to a heading or block of a page** (the import drops `#anchor`s).
- **Graph views:** not in the plan, per CLAUDE.md.
- **Backlinks in search results or the export.**

## Questions

1. **One run or two?**
   - **Two PRs, B.1a (index, backlinks, broken links) then B.1b (making links). Recommended:** each is reviewable, and B.1a is useful at once on the imported links.
   - One PR with everything.
2. **How a link is made:**
   - **`[[` and "Link to page" in the slash menu for a new link; `Ctrl+K` and the toolbar for selected text, which also take a web address. Recommended:** fast by keyboard, reachable by touch, and there is no way to make a web link by hand today either.
   - Only `[[` and the slash menu: no linking of selected text, and no web links by hand.
3. **Where the backlinks go:**
   - **A "Linked from" section below the content, on every screen size. Recommended:** always in reach, no extra control, works on the phone. The design's drawer can come with B.5.
   - The design's slide-out drawer on desktop, opened by a button, and the section below the content on the phone.

## For the test pass

B.1a's checks are in `docs/test-pass.md` ("B.1a"). B.1b adds its own when it is built:

- With your real library: a note you know is linked from others shows them under "Linked from".
- On the phone: make a link with `[[`, and with the toolbar on selected text.
- Trash a page that others link to: their links are struck through; restore it: whole again.

## Answers (the recommended ones, given when B.1 was planned)

1. **Two PRs:** B.1a (index, backlinks, broken links), then B.1b (making links).
2. **How a link is made:** `[[` and "Link to page" in the slash menu for a new link; `Ctrl+K` and the toolbar for selected text, which also take a web address.
3. **Where the backlinks go:** a "Linked from" section below the content, on every screen size.

## Handoff

### B.1a, built on Sonnet 5.5

Built on branch `feat/b1a-link-index`. **Not committed, not pushed.**

**Done**
- **Sidecar** (protocol version 2): `links.ts` finds the pages a document's link marks point to; `OP_COMPACT` answers with them as a sixth part. The server's version check and the test stub follow.
- **Data:** table `page_links` (migration `AddPageLinks`), `pages.links_indexed`.
- **Compactor:** writes the page's rows in its own transaction (new stage `LinksWritten`, so the crash tests cover it). `LinkIndex.ReplaceAsync` writes only what changed, and never a row for a link to the page itself.
- **Backfill job** (`LinkBackfill`, at start-up): indexes the pages below the current version in batches, under the compactor's advisory lock for each page (`Compactor.TryLockPageAsync`, shared). It reads and merges in the sidecar but writes no snapshot. A sidecar that is down stops it until it is back; a page that does not decode is counted and left.
- **Endpoint** `GET /api/pages/{id}/backlinks`: live pages only (`live_pages`), only in workspaces the person may view, by title with the parent's title, at most 200 and a `more` flag.
- **Web:**
  - the "Linked from" section (`BacklinksPanel`, below the content, asked again when the tab comes back and when the tree changes);
  - the broken-link decoration (`brokenLinks.ts`: struck through, with the reason in its title and after the link for a screen reader; nothing written to the content);
  - both languages.
- **Tests:**
  - sidecar: 25 new, including a 5,000-deep page;
  - server: `LinkIndexTests` (what a compaction writes, a crash at every stage, and a property test that the index is always the links of the snapshot, over random edits, compactions and crashes), `LinkBackfillTests`, `BacklinkEndpointTests`;
  - web: the extension and the panel, with Vitest;
  - Playwright: `backlinks.spec.ts` on desktop and both phones, plus a state in `a11y.spec.ts` on all four projects (axe, no sideways scroll, 44 px targets).
- **Mutation checks** (each removed for one run, its tests failing, then put back): the compactor's link write, the backfill's advisory lock, the refresh after a tree change, the once-only reader note, the panel's stale-answer guard.
- The e2e server compacts after 3 seconds of quiet instead of 2 minutes (`playwright.config.ts`), so a spec that needs a link in the index does not wait for minutes.

**Deviations from the plan**
- A broken link has two wordings, not by what the tree knows: "in the trash" when the page is in the trash list, and "not available: in the trash, or does not exist here" for everything else, since a page below a trashed parent is in neither list.
- A page in the trash still lists its own backlinks (the trash view opens it); the sources are what must be live.
- `web/e2e/importHelpers.ts`: `openImportPage` and `importJex` moved out of `import.spec.ts` so that two more specs use them.
- `SearchCompactionTests.A_compaction_that_does_not_change_the_text_…` starts from an already indexed page: the first compaction of a page now writes `links_indexed` once, which changes the row.

**Two fixes found by the full check, in code B.1a did not write**
- **`keyboard.spec.ts` flake (seen in run 1.22's check too):** Enter in the title asks the editor for the focus, and a page that has not finished syncing is not editable, so the request was dropped and the person's next keystrokes went nowhere. `PageEditor` now keeps a focus request for 5 seconds and carries it out the moment the editor can be typed into (three Vitest tests; the carry-out removed makes one fail).
- **`offline-copy.spec.ts` on iPhone (run 1.21's spec):** the Home key can come before the cursor has reached the end under load; the key sequence is tried again until the cursor is at the start of the last block (Home changes nothing once it is there).

**Checks:** `pnpm check` (2026-11-07), green: web 1209, sidecar 150, server 1928 with the timing tests, format and types clean, Playwright 361 passed and 74 skipped.

### Review fixes (`/code-review`, 10 findings, all fixed)

1. **A page that keeps taking the sidecar down** is left after three tries, until the next start; before, the backfill went back to it every 30 seconds without end, taking everybody's sync down each time.
2. **The job never stops the host:** each page's errors are caught (a page deleted meanwhile, a database that is gone for a moment), and so is a whole pass.
3. **Only a snapshot is read, and only its links:** the backfill takes pages with no live updates (the others are the compactor's, which batches and backs off), and a new links-only sidecar operation (`OP_LINKS`, `IYjsCodec.LinksAsync`) neither encodes nor reads back.
4. **`/p/<uuid>` only:** the server no longer counts a full address of any host, which the editor (same origin only) disagreed with and which could match another installation's page. B.1b stores a pasted address of its own origin as `/p/<id>`.
5. **The endpoint asks the page role of each source** (`GetPageRoleAsync`, where page shares arrive in run 2.2), stopping once one more than fits is found.
6. **A focus request is dropped** at the person's next key or click, so it never takes the focus from where they went on typing.
7. **Broken-link decorations are kept up to date by the change:** mapped along, and only the top-level blocks a step touched are looked at again (also a mark or attribute step, whose map is empty: a bug the new test found). Trash lookups are a set.
8. **No sidecar, no job:** it returns at once when the sidecar is off, and stops after twenty passes that met something in the way (about ten minutes).
9. **The heading never shows a number that is too low:** "Linked from more than 200 pages" when the server has more.
10. **The panel watches only the tree** (the view gives each page its own panel); the guard for overlapping asks stays, for a tree change while the tab comes back.

**Checks after the review fixes:** `pnpm check` (2026-11-07), green: web 1213, sidecar 152, server 1928 + 4 new (1932) with the timing tests, Playwright 361 passed and 74 skipped.

**Left for B.1b:** making links (decision 8 of the plan).

