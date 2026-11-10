# Run B.4: Quick switcher, command palette, and keyboard shortcuts

**Model:** S·med in the roadmap. One PR, branch `feat/b4-palette-shortcuts`.

**Status:** Implemented. No server, schema, dependency or architecture changes.

## Goal

A person gets to a favorite or recently opened page, searches their notes, opens a tag, or runs a common action without hunting through the sidebar. The same palette works by keyboard on the laptop and by touch on the phone. The existing way of making a link with Ctrl+K keeps working.

## Inputs

- [`docs/architecture.md`](../architecture.md): the B.4 row; "Making links" (Ctrl+K's two meanings); "Permission resolution"; "Accessibility and responsive layout"; the search performance target.
- [`docs/runs/1.17.md`](1.17.md), [`B.1b.md`](B.1b.md), [`B.2.md`](B.2.md) and [`B.3.md`](B.3.md): the existing search, link shortcut, tags, favorites and recent pages. B.2 and B.3 leave tags, favorites and recents in the palette to this run.
- [`docs/design/inkpact_slate/DESIGN.md`](../design/inkpact_slate/DESIGN.md), the [desktop palette](../design/desktop_command_palette_ctrl_k_modal/screen.png) and [mobile search](../design/mobile_quick_search/screen.png): visual references, not code. Ignore the graph, tasks, workspaces, manual compaction and split pane in the designs.
- [`docs/test-pass.md`](../test-pass.md): the hands-on checks below are deferred to run 2.10b.
- Code:
  - [`SearchDialog.vue`](../../web/src/search/SearchDialog.vue), [`search.ts`](../../web/src/stores/search.ts), [`shortcut.ts`](../../web/src/search/shortcut.ts), [`AppShell.vue`](../../web/src/layout/AppShell.vue) and [`SidebarContent.vue`](../../web/src/layout/SidebarContent.vue);
  - [`linkShortcutRegistry.ts`](../../web/src/editor/linkShortcutRegistry.ts) and [`linkShortcut.ts`](../../web/src/editor/linkShortcut.ts), which keep the editor's shortcut out of the shell's bundle;
  - [`favorites.ts`](../../web/src/stores/favorites.ts), [`recent.ts`](../../web/src/stores/recent.ts), [`tags.ts`](../../web/src/stores/tags.ts), [`tagSearch.ts`](../../web/src/tags/tagSearch.ts), [`tree.ts`](../../web/src/stores/tree.ts) and [`currentPage.ts`](../../web/src/stores/currentPage.ts);
  - [`router.ts`](../../web/src/router.ts), [`StartPage.vue`](../../web/src/pages/StartPage.vue), [`SidebarTree.vue`](../../web/src/tree/SidebarTree.vue) and [`TitleField.vue`](../../web/src/pages/TitleField.vue);
  - [`search.spec.ts`](../../web/e2e/search.spec.ts), [`keyboard.spec.ts`](../../web/e2e/keyboard.spec.ts), [`make-links.spec.ts`](../../web/e2e/make-links.spec.ts) and [`a11y.spec.ts`](../../web/e2e/a11y.spec.ts).

## What is there now (found while planning)

1. **Search is already a dialog.** Ctrl+K (Cmd+K on a Mac), the sidebar's Search row and the phone's search button open it. It searches the current workspace on the server after two characters and a 150 ms pause, with ranked page results, marked title and text segments, a path, loading and error states. Enter while a search is pending waits for the answer to the current text. The last query is selected when the dialog opens again.
2. **Ctrl+K has another meaning in the editor.** With a selection or the caret in a link, the shell yields to the editor's link picker; otherwise it opens search. The shell listens in the capture phase and asks the import-free registry first. An open dialog is not covered by another one; the phone drawer closes before search opens.
3. **Favorites and recents are per user on the server.** Their endpoints return live pages only; favorites are in starring order and visits newest first. The stores hold ids and use the live tree for titles. A page made on another device can be in a list before this tab's tree knows it. The current list reads silently ignore errors, so the palette needs an observable failure rather than presenting a failed read as an empty list.
4. **Tags have a list and a destination.** The workspace tag store reads the existing endpoint; `findTags` matches names ignoring case and accents, with starts before contains. A tag opens the existing `tag` route. The tree and these lists are workspace-scoped; page shares are later work.
5. **Some shortcuts already exist.** The tree has F2, Delete and Alt+Arrow operations; the editor has Alt+Up/Down to move a block. The keyboard walk and the link tests cover the existing behavior.
6. **A closed palette must be cheap.** B.1b found that an always-mounted picker searching the whole tree while closed made iPhone editing flaky. The palette must not filter lists or fetch data while it is closed.

## Decisions

These are the planning defaults for this run. They keep the existing search instead of making a second, competing search.

1. **One dialog, three scopes:** Pages, Tags and Commands, with Reka UI tabs and the existing Dialog/Listbox patterns. The sidebar, phone button and Ctrl+K open Pages; Ctrl+Alt+K (Cmd+Option+K) opens Commands. Tabs are reachable by touch and keyboard. The active scope has one labelled field and one results list.
   - **Pages, empty field:** up to five favorites in starring order, then up to five recent pages newest first. Omit a page from Recent when it is already shown in Favorites. Show its title and parent where available, including the translated untitled fallback. Empty lists have a clear empty state.
   - **Pages, text:** preserve the existing server search, its two-character minimum, ranking, snippets, highlights, 150 ms debounce, errors and Enter-before-the-answer behavior. Search still finds title, tags and content; do not replace it with a title-only or client-side search. The previous page-search query is retained and selected on reopening, as it is now; clearing it reveals Favorites and Recent.
   - **Tags:** read the workspace's existing tag list when this scope is first opened during a palette session. With an empty field show the first eight; otherwise use `findTags`, at most eight, and say when more match. Choosing one opens its existing tag page. No tag-search syntax is added to page search.
   - **Commands:** show the available commands below, filtered by their translated names with the same case- and accent-folding rule as tags. An empty field lists them all. Tags and Commands each start with an empty field when the palette is opened; neither changes the saved page-search query.
2. **Commands use existing actions and permissions:**

   | Command | Result | Available when |
   | --- | --- | --- |
   | New page | Create a blank **top-level** page with `tree.create`, open it and focus its title | Workspace owner or editor |
   | Add to / Remove from favorites | Toggle the current page with the existing favorites store | A live current page and a successfully read favorite state; viewers may use it |
   | Go to start | Open the start page | A workspace is open |
   | Open tags | Open the Tags view | A workspace is open |
   | Open trash | Open the Trash view | A workspace is open |
   | Import from Joplin | Open the existing import page | Workspace owner or editor |
   | Keyboard shortcuts | Show the shortcut help inside the palette | A workspace is open |

   The command list and its handler both check availability; the server's existing policies remain authoritative. Do not offer the favorite command for a missing page or one in the trash. Close the palette before navigating or making a change, so the existing error notice is visible if an action fails. A failed create does not navigate; a failed favorite toggle rolls back as it does now. A create is single-flight, including across a shortcut and the palette: holding a key or choosing twice cannot make two pages. Only focus the title of the page actually created. If the person navigated or moved focus elsewhere while the request was pending, do not interrupt them: show a translated notice with an Open action for the new page instead.
3. **Keyboard shortcuts:**

   | Windows/Linux | Mac | Action |
   | --- | --- | --- |
   | Ctrl+K | Cmd+K | Open Pages; in the editor, a selection or caret in a link still opens the link picker |
   | Ctrl+Alt+K | Cmd+Option+K | Open Commands directly |
   | Ctrl+Alt+N | Cmd+Option+N | Create a top-level page, for an owner or editor, when focus is not in an editable control |

   The two new combinations are proposed defaults: check the supported browsers and operating systems for reserved bindings before implementing them, and update this card if one must change. Do not take Ctrl+N or Cmd+N from the browser. The direct create shortcut is deliberately inactive in inputs, textareas, selects and contenteditable editors; the command remains available through the palette while editing.

   Ignore repeated keydowns, IME composition and AltGraph for the new shortcuts; require exactly the listed modifiers and leave unrelated keys and native text entry alone. Match letters and non-Latin layouts by the existing shortcut's rule. Only prevent the default for a new shortcut that is actually handled. Do not run a direct shortcut over an open modal or menu. Opening the palette from the drawer closes it first; do not open it over another dialog. Preserve the existing Ctrl+K handling, including yielding to the editor and not opening over the link picker.
4. **Shortcut help** is reachable from a visible button and the command list, inside the same dialog rather than a nested modal. It names the new shortcuts, Ctrl+K's link behavior, the existing tree F2/Delete/Alt+Arrow keys and editor Alt+Up/Down, with their contexts. Show Ctrl or Cmd/Option for the platform. Back returns to the previous palette scope, and Escape closes the dialog.
5. **Freshness and failures:**
   - Read favorites and recents when the Pages scope opens, and again on the next palette opening; a recent read still waits for visits being sent. Read favorites before offering the toggle command. Read tags on entering Tags, not on every key. Coalesce repeated reads within an opening.
   - Expose read success and failure from the shared stores without breaking their existing callers. A failed list shows a translated error and Retry in that scope, not an apparently empty or actionable stale list; another successful section can still be used. Keep the existing notices for failed actions.
   - Resolve page ids through the current live tree. If a freshly read list names an id the tree does not yet know, refresh the tree once and resolve again; report a failed refresh, and omit ids still not in the live tree. A refresh must not overwrite a newer tree change. Guard in-flight reads by workspace and opening/generation, so a late answer after closing, switching workspace, logout, a newer read or a local change cannot replace newer state.
   - Cancel or invalidate a pending full-text search when leaving Pages or closing the palette. A late result must not select or open a page for a different query or scope. Reopening Pages with the saved query searches again. Do no list filtering, tree work or background palette reads while closed; an action explicitly chosen before closing may finish.
6. **Accessible and responsive:** use the existing design tokens in both themes, with no new dependency, inline style attributes in templates or CSP exception. The dialog has a name and description and puts focus in the active scope's field on opening. Tabs, fields, grouped options, loading, errors, empty states and counts have appropriate labels and announcements in English and Dutch; format counts with `Intl`. The tabs are reachable by Tab and work with arrow keys; in the results, arrow keys move the active option, Enter selects it, Escape closes, and the active option stays visible. Never execute an option from an older query or scope. The focus is trapped while open and returns to the opener on dismissal, or to the phone's menu button if its drawer was closed. A chosen page uses the existing router and page announcement. On a 320-pixel phone the controls and results fit, are scrollable with the keyboard open, and touch targets are at least 44 pixels.
7. **No server or schema change.** Use the existing authorized endpoints and stores, and no new package. When page shares arrive in phase 2, the existing search, favorites, recent and tag lists must honor the permission rules for the current user; this palette must not create a second source of access decisions.

## Build order

1. Extend the existing search dialog and store for scopes, preserving page-search behavior and the editor shortcut registry. Add the tabs, the blank Pages state and the Tags scope.
2. Add a typed command list and one dispatcher shared by the palette and the direct create shortcut. Wire the existing actions, availability checks, pending state, shortcut help and focus behavior.
3. Expose and handle list-read failures and the missing-tree-id refresh without losing newer local changes. Add both translations and the desktop/phone styling.
4. Add the tests below, run the focused checks, and record the actual build and any deviations in this card. If the implementation proves a frozen architecture decision wrong, propose and update `docs/architecture.md` in that PR; do not change it merely to record this plan.

## Done when

- Ctrl+K, the sidebar and the phone button open Pages; the direct command shortcut opens Commands. Ctrl+K with selected editor text or the caret in a link still opens the link picker, and no palette opens over another dialog.
- Favorites and recents appear in their stated order without duplicates; a page opened or starred on another device appears after reopening the palette when it falls within the displayed limits, including when its id was not in this tab's tree. Trashed or inaccessible pages are not offered.
- Page search retains its current matching, ranking, marks, errors, saved query and pending-Enter behavior. A tag and each available command can be found and chosen; disabled contexts cannot run a write.
- The new shortcut creates one page, never consumes normal input or an AltGraph/IME key, and a refused request is reported. The command and shortcut cannot create duplicates while a request is pending.
- Every scope, help and failure state is usable with keyboard and touch, announced by a screen reader, translated in both languages and readable in both themes. Focus and the mobile drawer behave as described; CSP stays clean.

## Tests

- **Vitest:** scope and query isolation; favorites/recent order, deduplication, empty and missing ids; tag and command matching, limits and translations; role, current-page and trash guards; create/favorite dispatch, error, rollback and single-flight; creating while the person navigates or moves focus elsewhere and opening the page from the notice; list and tree refresh failures and retries; out-of-order reads, pending visits, local changes, close/reopen and logout; no filtering or background reads while closed. Keep the existing search tests for debounce, abort, current-query Enter, highlighting and the selected last query.
- **Shortcut and component tests:** Ctrl and Cmd, exact modifiers, non-Latin fallback, repeat, composition, AltGraph, input/editor focus and other dialogs; Ctrl+K yielding to the link picker; drawer close and focus restoration; tab, arrow, Enter and Escape behavior, stale highlights and shortcut help.
- **Playwright:** a new palette spec on desktop, Pixel 7 and iPhone for favorites, recents, tags, commands and failure states; extend `search.spec.ts`, `keyboard.spec.ts` and `make-links.spec.ts` for search and link regressions and the new keyboard paths. Extend `a11y.spec.ts` with each scope and help, including the 320-pixel project and both themes; keep CSP checks enabled. Use the existing real-server fixtures, not a mocked backend for the main journeys.
- **While building:** `pnpm --filter web exec vitest related <changed files>`, web lint and typecheck, and the affected Playwright specs. Preserve the existing 10,000-page search timing target (95th percentile under 200 ms) and verify that a closed palette does no work on tree changes. Before each push run `pnpm check:push` plus the affected Playwright specs; before the last push run `pnpm check` once with Postgres up.

## Docs to update

- [`docs/test-pass.md`](../test-pass.md): B.4's checks are recorded there now; they are not a gate for building this run.
- This card: add the handoff, tests run and any deviations when built. Update the architecture only if implementation proves a decision wrong, as above.

## Implementation handoff

- Extended the existing search dialog into Pages, Tags and Commands scopes. Pages retains server search and adds favorites/recents; Tags loads lazily; Commands are translated, filtered and permission-checked both when displayed and dispatched.
- Added the global command/new-page shortcuts, single-flight page creation, focus-safe title opening or a translated Open notice, observable list-read errors/retries, and fresh tree resolution for list ids.
- Added component/store coverage and a real-server Playwright palette journey, including desktop, Pixel 7 and iPhone. The previous search, keyboard, link and accessibility browser specs still pass.
- Verification: `pnpm check:push` passed (1,985 non-slow backend tests), followed by full `pnpm check`: lint/type checks, web (74 files/1,490 tests), sidecar (7 files/152 tests), .NET formatting, the unfiltered backend suite including slow timing tests (1,989 passed, none skipped), and all Playwright projects (417 passed, 81 skipped). Playwright built the production bundle and ran against the real server with Postgres; CSP assertions remained enabled.
- The first full run exposed a palette test assumption: its tag is not necessarily among the first eight in a populated workspace. The test now searches for its unique tag before selecting it; the subsequent full run passed.
- Deferred: cross-platform layout, AltGraph, Mac Option, mobile on-screen keyboard and screen-reader checks remain in the manual test pass below; automated results do not certify those combinations.

## Out of scope

- Fuzzy search, new search syntax or ranking, cross-workspace search, and changes to search indexing.
- Customizable key bindings, more global shortcuts, destructive commands, and duplicate implementations of import, export or tree operations.
- Offline/PWA behavior (B.6), version history (B.5), to-dos, reminders, sharing and multiple workspaces (phase 2).
- The graph, source pane, manual compaction and claims about signed updates in the reference designs.

## For the test pass

- On the phone, open the palette with the on-screen keyboard up; switch between pages, tags and commands, scroll the results, open a page and a tag, and check that the keyboard does not cover the chosen item.
- Star and open pages on one device, then open the palette on the other and clear the Pages field: Favorites and Recent are current, do not repeat a page, and no trashed page is offered.
- On the laptop, check Ctrl+K in the editor with and without a selection and with the caret in a link; try the command and new-page shortcuts, including in an input and with a dialog open. Check a non-Latin or AltGraph layout and Cmd/Option on a Mac if available.
- With TalkBack on the phone and a screen reader on the laptop, hear the active scope, results, errors and shortcut help; move through the list and dismiss it back to the opener, or the menu button when the drawer was closed.
- With the real Joplin library, search and switch pages quickly, and type in the editor with the palette closed: neither action should feel held up by the palette.
