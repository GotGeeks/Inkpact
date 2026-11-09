# Run B.2: Tags in the interface

**Model:** S·med in the roadmap. Planned on Opus 5.5 high; builds on Sonnet 5.5 medium. `/code-review` at high on Opus before the PR merges: four new endpoints with their authorisation and a concurrent write path. One PR, branch `feat/b2-tags`.

## Goal

A person sees a page's tags under its title, adds a tag (an existing one or a new one) and removes one, on the desktop and on the phone. A "Tags" view in the sidebar lists the workspace's tags and, for each, the pages that have it, so a library tagged in Joplin can be browsed by tag from the first day. Tag changes are add and remove operations on a set, so two people (or two tabs) changing a page's tags at once both keep their change.

## Inputs

- `docs/architecture.md`: the B.2 row; the data model row `tags`, `page_tags`; "Tree and metadata conflicts" (the "Tag changes" row); the search columns (`tag_text`, kept by triggers); "Importing again" (tags are a set in the three-way rules, and when a tag is renamed); "Permission resolution"; `live_pages`.
- `docs/design/inkpact_slate/DESIGN.md`: "Inline Elements & Tags" (chips are `rounded-full`), the type scale, touch targets. The designs `desktop_workspace_split_pane_editor` (a row of tag pills under the title with an add button) and `mobile_active_editor` (the same row, wrapping, "Add tag").
- `docs/test-pass.md`: manual checks go there, not into this run.
- Code:
  - `server/src/Inkpact.Data/Entities/Tag.cs`, `InkpactDbContext.cs` (unique lower-case name per workspace);
  - `server/src/Inkpact.Api/Pages/PageEndpoints.cs`, `TreeService.cs` (`GetPageAsync`, `LockAndLoadAsync`, `page_in_trash`), `Links/BacklinkEndpoints.cs` (a list of live pages with their parent's title, a cap and `more`);
  - `Import/ImportRunner.cs` (`name.ToLowerInvariant()` for a tag), `ImportRunner.Fields.cs` and `ThreeWay.Tags`;
  - `web/src/pages/PageView.vue`, `TitleField.vue`, `BacklinksPanel.vue`; `web/src/stores/tree.ts` (the change-then-notice pattern); `web/src/tree/SidebarTree.vue` and the trash link; `web/src/editor/pageSearch.ts` (folding case and accents); `web/src/router.ts`;
  - `web/e2e/importHelpers.ts`, `backlinks.spec.ts`, `a11y.spec.ts`.

## What is there now (found while planning)

1. **Tags are stored, never shown.** The Joplin import makes `tags` and `page_tags` (run 1.15) and keeps them in step on a re-import (run 1.16). No endpoint reads or changes them; `PageDetail` has no tags.
2. **Search already finds them.** `tag_text` is kept by triggers on `page_tags` and `tags`, so any change of a tag, by any code, reaches search with no code here (run 1.17). The export writes them in the front matter (run 1.18).
3. **The re-import already respects changes made here.** Tags are compared as a set against the base: a tag removed here stays removed unless Joplin changed that tag on the note since; a tag renamed here keeps its name here. A tag deleted outright would be imported again, as any item that is gone.
4. **A tag's name is unique per workspace in lower case** (`ToLowerInvariant`, the import's rule), at most 200 characters. Two requests that make the same new tag at once would meet the unique index.
5. **No change reaches another tab by itself.** The tree is read again after the tab's own changes and when a page opens; the backlinks panel also asks again when the tab gets focus. Tags can do the same.
6. **The design's sidebar** has a "Tags & Taxonomy" entry; the plan has no taxonomy, only a flat set of tags per workspace.

## Decisions

1. **The page's tags come with the page:** `PageDetail` gets `tags: [{ id, name }]`, sorted by their lower-case name. No second request when a page opens.
2. **Changing a page's tags is one set operation:** `POST /api/pages/{id}/tags` with `{ add: string[] (names), remove: string[] (tag ids) }`, `RequirePageRole(Editor)`. It answers with the page's tags as they now are.
   - **Adding by name:** the name is cleaned by the import's rule (control characters out, tabs as spaces, trimmed; inner spaces kept, so a typed name finds the tag the import made from it; changed in review, the plan said to make inner white space one space). A tag with that name in lower case is the one used, with its own spelling; otherwise a tag is made with the name as typed. Adding a tag the page has does nothing.
   - **Removing by id:** only the page's own `page_tags` rows are touched, so an id of another workspace or one the page does not have does nothing and tells nothing.
   - **Both at once:** removes first, then adds.
   - **Limits and errors** (codes, translated by the client): `tag_name_empty`, `tag_name_too_long` (over 200), `too_many_tags` (more than 100 names and ids in one request), `page_in_trash` (409, as a rename: a page's metadata is read-only in the trash; its content is not).
   - **Concurrency:** the page row is locked (`LockAndLoadAsync`), so two changes of one page run one after the other, each on the set as the other left it. Two pages making the same new tag at once: the second meets the unique index, and its transaction is run again once, which then finds the tag. No raw SQL.
   - **What it does not touch:** the page's `version`, `updated_at` and the workspace's tree version. Tags are not the tree's title or place, and the re-import's base is unaffected.
3. **The workspace's tags:** `GET /api/workspaces/{id}/tags`, `RequireWorkspaceRole(Viewer)`: `[{ id, name, pageCount }]`, counting live pages only (`live_pages`). A tag on no live page is left out. Sorted by lower-case name. No cap: a library has hundreds, not tens of thousands.
4. **The pages of a tag:** `GET /api/workspaces/{id}/tags/{tagId}/pages`, `RequireWorkspaceRole(Viewer)`: the live pages with that tag, with id, title and the parent's title, by title, at most 200 and a `more` flag: the shape of the backlinks endpoint, sharing its code where that is less code. A tag of another workspace is `not_found`.
5. **Renaming a tag** (question 2): `PATCH /api/workspaces/{id}/tags/{tagId}` with `{ name }`, `RequireWorkspaceRole(Editor)`. The same name rules; `tag_name_taken` (409) when another tag has the name in lower case; a change of case only is allowed. *(Changed in review: `tag_name_taken_in_trash` when only pages in the trash have the other tag, and a tag no page has is taken over: it goes.)* There is no delete: a tag that is on no live page is not listed and not suggested, and is used again when its name is typed.
6. **The tag row on a page** (`pages/PageTags.vue`, question 1), between the title and the child pages:
   - **Pills:** `rounded-full`, `label-md`, `bg-surface-container-high`, the name after a `#` that a screen reader does not read. Each opens that tag's pages. With edit rights, each has a remove button ("Remove tag {name}"), 44 pixels on touch.
   - **Adding:** an "Add tag" button opens a field in the row (Reka UI `Combobox`), focus in it. It suggests the workspace's tags whose name contains what is typed (case and accents folded as in `pageSearch.ts`, names that start with it first, at most 8, not those the page has), and "Create tag "{name}"" first when no tag has exactly that name. Enter or a click adds and keeps the field open for the next; Escape closes it and gives the focus back to the button.
   - **Read-only** (viewer, or the page in the trash): pills without remove buttons and no add button; no row at all when there are no tags.
   - **Freshness:** the row shows what the server answered; it reads the page's tags again when the tab gets focus, as the backlinks panel does. Errors become notices, as in the tree store.
7. **The Tags view** (question 1):
   - **Where:** a "Tags" link in the sidebar next to the trash, routes `/tags` and `/tags/:tagId`, lazy like the other views.
   - **`/tags`:** the tags with their page counts (`Intl` numbers), a filter field, each opening its pages.
   - **`/tags/:tagId`:** the tag's name, renameable in place for an editor (the pattern of `TitleField`), and its pages (title, the parent in small type), each opening the page; "More than 200 pages" when the server says `more`.
   - **The store:** `stores/tags.ts` holds the workspace's list, read when the add field or the view opens and after any change.
8. **i18n and accessibility:** every label and error code in English and Dutch. The row is a list ("Tags") with labelled buttons; the field is a labelled combobox with the slash menu's way of announcing the number of results. The `a11y.spec.ts` states get the row with the field open and both views, on all four projects.

## Done when

- An imported page shows its Joplin tags under its title, on desktop and phone.
- A tag can be added (existing or new) and removed, with the keyboard and by touch; the change is in search and in the export with no further step.
- Two changes of one page's tags at once both survive; two pages making the same new tag at once end with one tag.
- `/tags` lists the tags of live pages with counts; a tag lists its live pages; an editor can rename a tag, and a name in use is refused.
- A tag removed or renamed here is still removed or renamed after the same Joplin export is imported again.
- A viewer and a page in the trash show the tags without a way to change them; the server refuses the change too.
- Both languages, axe clean on the new states on every project, CSP clean.

## Tests

- **Server:**
  - the set operation: add existing (its spelling kept), add new, add twice, remove, remove absent, remove another workspace's id (nothing happens), both in one request, the name rules and limits, `page_in_trash`;
  - concurrency: parallel requests on one page with different tags all land; parallel requests on two pages with the same new name make one tag;
  - property test (FsCheck): after a random sequence of set operations the page's tags equal a model set, and the tag rows are unique per lower-case name;
  - the list: live pages only (trash and under trash not counted), a tag on none left out, order;
  - the pages of a tag: live only, parent title, the 200 cap, another workspace's tag `not_found`;
  - rename: case only, `tag_name_taken`, editor only;
  - `PageDetail` has the tags;
  - with the import: a tag removed here and a tag renamed here survive a re-import of the same export;
  - the deny-by-default policy test covers the four new endpoints; a viewer is refused the two that change.
- **Vitest:**
  - `PageTags`: pills, remove, the field (suggestions folded and filtered, "Create tag", Enter keeps it open, Escape gives the focus back), read-only, empty, an error becomes a notice, reload on focus;
  - the tags store; both views (list, filter, empty, `more`, rename and its errors).
- **Playwright** (`tags.spec.ts`, desktop and both phones):
  - import a JEX with tags (`importHelpers`) and see them on a page; add an existing and a new tag, remove one; open a tag from a pill and from `/tags`; rename a tag;
  - `a11y.spec.ts`: the row with the field open, `/tags`, `/tags/:id`, all four projects; CSP clean.

## Docs to update

- `docs/architecture.md`: the data model row (shown from B.2, no delete), the "Tag changes" row (the endpoint and the lock), the endpoints, and in "Importing again" that a change of tags here is kept by the next import.
- `docs/test-pass.md`: B.2's checks.

## Out of scope

- Deleting or merging tags (question 2), tag colours, nested tags.
- Filtering by several tags at once, and a `#tag` syntax in search (tags already match by their words).
- Tagging many pages at once, and tags when making a page.
- Tags in the command palette (B.4).

## Questions

1. **Where tags show and how to browse by them:**
   - **A row of pills under the title, and a "Tags" view in the sidebar with each tag's pages. Recommended:** the design's row, and browsing by tag is what Joplin's sidebar gives today.
   - Only the row; finding pages by a tag goes through search.
2. **Managing tags:**
   - **Rename, no delete; a tag on no live page is not listed. Recommended:** renaming is what a tidy-up needs, and the re-import keeps a rename here. A deleted Joplin tag would come back with the next import.
   - Rename and delete (from every page at once).
   - Neither: tags are only added and removed on pages.

## For the test pass

(Added to `docs/test-pass.md` when the run is built.)

- With your real library: a note you tagged in Joplin shows its tags, on the laptop and on the phone; `/tags` lists them with sensible counts.
- On the phone, with the on-screen keyboard open: add an existing tag and a new one, remove one.
- Rename a tag, remove a tag from a note, then import the same Joplin export again: both changes are still there.
- With TalkBack (phone) or a screen reader (laptop): the row and the add field are announced, with the number of suggestions.

## Answers (the recommended ones, given when B.2 was planned)

1. **Where tags show:** a row of pills under the title, and a "Tags" view in the sidebar with each tag's pages.
2. **Managing tags:** rename, no delete; a tag on no live page is not listed.

## Handoff

Built on branch `feat/b2-tags`, on Sonnet 5; the review fixes on Opus 5.5.

**Done**
- **Server** (`Tags/`): `TagService` and `TagEndpoints`, the four endpoints of decisions 2 to 5; `PageDetail` has the page's tags. `TagNames` is the one name rule, shared with the import.
- **Web:** the tag row (`pages/PageTags.vue`) under the title, the "Tags" view and a tag's own page (`tags/`), the `tags` store, a "Tags" link in the sidebar, both languages.
- **Tests:** `TagServiceTests`, `TagEndpointTests`, an FsCheck model test (`TagPropertyTests`), two re-import tests in `ReimportFieldsTests` (a removal and a rename made here survive); Vitest for the row, the store, the search helpers and both views; Playwright `tags.spec.ts` and a state in `a11y.spec.ts`.

**Deviations from the plan**
- **The page row is locked `for update`, not with `LockAndLoadAsync`:** that one raises the workspace's tree version, which decision 2 says a tag change leaves alone.
- **The add field is an input with a listbox in the ARIA combobox pattern,** not Reka UI's `Combobox`, as the other menus of the app are.
- **The workspace's id is found by the tags store itself** (`tree.init()` when nothing has), so the views work when opened on their own.

**Found by the full check, fixed:** the tag row's add control was a plain child of `role="list"` (invalid, and the row is on every page, so it failed the axe scan of many screens); the rename field was under 44 pixels on touch.

**Review fixes** (`/code-review`, 10 findings, all fixed):
1. **No older answer replaces a newer one:** a row sends its changes one at a time and reads the tags again on the same queue; it no longer follows the page the view reads, which could be older.
2. **One name rule for the import and the interface** (`TagNames.Clean`): inner spaces are kept (the plan said to make them one space), so a name typed here finds the tag the import made from it; control characters are removed rather than reaching Postgres.
3. **"Same name" on the client is the server's rule:** case ignored, accents not, so "cafe" can be made next to "café".
4. **The highlighted option goes back to the first** when the text changes.
5. **The focus comes back:** to "Add tag" after Escape; to the next pill, or "Add tag", after a remove.
6. **The field is focused before the workspace's tags are read,** so a phone opens its keyboard on the first tap.
7. **Enter that confirms a word in an input method** is left to it, in the add field and in the rename field.
8. **Rename:** a tag no page has is taken over (it goes); one only pages in the trash have gives `tag_name_taken_in_trash`; two renames to one name at once give one `tag_name_taken`, not a 500.
9. **A tag's pages are sorted and cut to 200 in the database;** the list of pages with their parent's title (`ListedPage`) is shared with the backlinks.
10. **The Tags view says it is loading, or could not load** (with a notice), never "no tags yet" before it knows, and says so when the filter matches nothing.

Each fix has a test that fails without it (mutation-checked).

**A spec this run broke, fixed in its fixture:** `editor-blocks.spec.ts` "the slash button opens the menu, which stays out from under the toolbar" failed on the iPhone in every full run, and 4 of 4 alone; it passed 6 of 6 on `main`. The phone specs' fake visual viewport copied `window.innerHeight` once at the start of the page, when WebKit still reports 1643 pixels on the iPhone, so the menu's placement never kept clear of the toolbar. On `main` the menu ended half a pixel above the toolbar by luck; the tag row, 44 pixels on touch, moved the content down. The fake now reads its size from the window each time, as a real one does; the app was not at fault. (First taken for an older flake: it was checked on `main` only after the PR was opened.)

**Not done:** the manual checks, in `docs/test-pass.md` under "B.2".
