# Run B.1b: Making links in the editor

**Model:** S·high in the roadmap (the B.1 row). Planned on Opus 5.5 high; builds on Sonnet 5.5 high. `/code-review` at high on Opus before the PR merges. The second half of B.1: `docs/runs/B.1.md` is the card of the whole run, and its decision 8 and answer 2 are what this builds.

## Goal

A person can make a link from the editor: to another page by typing `[[` or by choosing "Link to page", and from selected text to a page or a web address, on the desktop and on the phone. A link can be changed and removed. Every page link the editor makes is `/p/<id>`, which is what the link index of B.1a counts. So a link made now shows under "Linked from" on the other page after its next compaction, and is struck through when that page goes to the trash.

## Inputs

- `docs/runs/B.1.md`: decision 8, answers 2 and 3, the review fixes of B.1a (fix 4: the server counts `/p/<uuid>` only).
- `docs/architecture.md`: "Backlinks and broken links", "Links between notes" (how a link opens; `links.ts`), "Quality standards" (accessibility, the 320-pixel project).
- `docs/design/inkpact_slate/DESIGN.md`: tokens, touch targets.
- Code:
  - `web/src/editor/slashExtension.ts`, `SlashMenu.vue`, `blockCommands.ts`: the slash menu, a TipTap `Suggestion` with the editor as the combobox, which keeps the focus and names the chosen option through `aria-activedescendant`.
  - `web/src/editor/EditorToolbar.vue`: the phone's toolbar. `LinkMenu.vue`: the menu under a link, with "Open" only. `links.ts`: `pageIdOfLink`, `isAllowedLink`. `extensions.ts`: TipTap Link with autolink and link on paste.
  - `web/src/search/shortcut.ts` and `layout/AppShell.vue`: `Ctrl+K` opens search from anywhere, "also inside the editor".
  - `web/src/stores/tree.ts`: the live pages of the workspace, `byId`. `tree/treeModel.ts`: `ancestorIds`.
  - `web/e2e/backlinks.spec.ts`, `a11y.spec.ts`, `importHelpers.ts`; the e2e server compacts after 3 seconds of quiet.

## What is there now (found while planning)

1. **No way to make a link by hand,** to a page or to the web. Links come from typing or pasting a web address (autolink, link on paste), from pasting a page address, and from the import.
2. **`Ctrl+K` is taken:** it opens search everywhere, the editor included (1.17, and the keyboard walk of 1.19).
3. **The slash menu's pattern fits `[[`:** `@tiptap/suggestion` is a dependency already and allows a trigger of two characters. The menu's look, keys and screen-reader pattern can be shared.
4. **A pasted page address is stored in full** (`https://host/p/<id>`). The editor opens it as a page (`pageIdOfLink` reads both forms), but since B.1a's review the server counts `/p/<id>` only, so such a link has no backlink.
5. **The tree store has every live page of the workspace,** with titles and parents. That is enough to find a page by its title at once, without asking the server.

## Decisions

1. **`[[` opens a page menu at the cursor:** a `Suggestion` with the trigger `[[`, after a space or at the start of a line, never in a code block or a read-only editor.
   - It lists the live pages whose title contains what is typed after `[[`: case and accents ignored, titles that start with it first, at most 8. The current page is left out.
   - Two pages with one title show their parent's title.
   - Enter or a click replaces `[[query` with the page's title as text with a link mark, `href="/p/<id>"` ("Untitled" for a page without a title), and the cursor after it. The link does not grow when typing goes on (TipTap's link mark is not inclusive).
   - Escape closes it and keeps what was typed.
   - With no match it says "No pages found" and Enter does nothing.
   - The look and the keys are the slash menu's, in a component of its own (`PageLinkMenu.vue`) on a shared base where that is less code than two copies.
2. **"Link to page" in the slash menu** (icon: link) replaces `/query` with `[[`, which opens the page menu: one way in for the person, one code path for the link.
3. **Linking selected text** (question 1):
   - **How it opens:** `Ctrl+K` (`Cmd+K` on a Mac) in the editor with text selected opens a link picker. Without a selection, and outside the editor, `Ctrl+K` opens search as now. The picker is a small popover under the selection, with one field (Reka UI Combobox, focus in the field).
   - **What it offers:** the same page list as `[[`, filtered by what is typed.
   - **A web address:** when what is typed is an allowed web address (`isAllowedLink`: `http`, `https`, `mailto`), the first option is "Link to <address>". A page address of this app becomes `/p/<id>`.
   - **Choosing** puts the link mark on the selection (its text stays) and gives the focus back to the editor, with the cursor after the link. Escape closes it and gives the focus back with the selection as it was.
   - **Inside a link** with no selection, `Ctrl+K` takes the whole link and opens the picker on it, to change where it points.
4. **The phone's toolbar** gets a "Link" button.
   - With text selected it opens the picker.
   - Without, it types `[[` at the cursor, which opens the page menu.
   - The desktop has no toolbar, and gets `Ctrl+K`, `[[` and the slash menu.
5. **The link menu** (under a link while editing) gets "Change link" (the picker on the whole link) and "Remove link" (the mark off the whole link, the text stays), next to "Open". Both only when the page can be edited.
6. **Pasted page addresses are stored as `/p/<id>`:**
   - **The rule:** a link mark that this client makes, by paste, by autolink or by the picker, whose address is a page of this app on this origin (`pageIdOfLink`), gets `/p/<id>`.
   - **How:** an `appendTransaction` plugin that looks only at the ranges a local transaction changed, and never at one that came from another client through Yjs. Rewriting those would write edits nobody made.
   - **Earlier links:** links pasted before this run stay in full. They open as pages, but have no backlink until they are pasted again (written in the architecture).
7. **i18n and accessibility:**
   - **Words:** every label in English and Dutch.
   - **The page menu:** the slash menu's ARIA pattern.
   - **The picker:** a labelled combobox ("Link to"), each option a page title with its parent for a screen reader.
   - **Checks:** the axe states of `a11y.spec.ts` get the page menu, the picker and the link menu with its new items, on all four projects, with 44-pixel targets on touch.

## Done when

- `[[` and "Link to page" make a link to a page, with its title as text and `/p/<id>` as address, on desktop and phone; the link shows under "Linked from" on the other page after its compaction.
- With text selected, `Ctrl+K` and the phone's Link button link it to a page or to a web address; without a selection `Ctrl+K` still opens search.
- A link can be changed and removed from the link menu.
- A page address pasted from the address bar is stored as `/p/<id>`; a link that came from another client is never rewritten.
- Both languages; axe clean on the new menus on every project; CSP clean.

## Tests

- **Vitest:**
  - the page menu: trigger only after a space or at the start, not in code or read-only; filtering (case, accents, prefix first, at most 8, not the current page, parent for same titles); Enter and click insert title and `/p/<id>`; "Untitled"; Escape keeps the text; no match.
  - the slash item: it opens the page menu.
  - the picker: page and web address options; `javascript:` and a relative address not offered; the selection keeps its text; Escape gives the focus back with the selection; changing an existing link.
  - `Ctrl+K`: link picker with a selection in the editor, search otherwise (no selection, title field, sidebar).
  - the toolbar button, both ways.
  - the link menu's "Change" and "Remove".
  - normalisation: a paste and an autolink of a same-origin page address become `/p/<id>`, another host's stays, a remote Yjs update is not touched.
- **Playwright:**
  - desktop: `[[` to a page, then the backlink after compaction (the B.1a pattern); `Ctrl+K` on a selection to a page and to a web address; remove a link.
  - phone and iPhone: the toolbar's Link button on a selection, and without.
  - `a11y.spec.ts`: the three new states, all four projects.
  - the keyboard walk (`keyboard.spec.ts`) still opens search with `Ctrl+K`.

## Docs to update

- `docs/architecture.md`, "Links between notes": how a link is made, `Ctrl+K`'s two meanings, and the normalisation (and that older full-address links stay).
- `docs/test-pass.md`: B.1b's checks.
- `docs/runs/B.1.md`: the handoff points to this card.

## Out of scope

- Links whose text follows the page's title (mentions): a node type of their own.
- Links to a heading or block.
- Making a new page from the page menu ("Create page …"), as Notion offers. It is a tree operation from inside the editor, and worth a decision of its own (question 3).
- Search of the whole text in the page menu: titles only.

## Questions

1. **`Ctrl+K` in the editor:**
   - **With text selected it makes a link; without, it opens search, as everywhere. Recommended:** this is what Notion and Google Docs do, and the keyboard walk's `Ctrl+K` (no selection) still works.
   - Another key for links (for example `Ctrl+Shift+K`), and `Ctrl+K` stays search only.
   - `Ctrl+K` is a link everywhere in the editor, and search needs a click or another key there.
2. **Where the page menu finds pages:**
   - **By title, in the tree this tab has: at once, offline too. Recommended.**
   - Through search on the server: also words in the text, but a round trip per key and nothing offline.
3. **"Create page" in the page menu** when no page matches:
   - **Not now. Recommended:** the plan has no such item, and it makes pages from inside the editor (where, in which notebook) that need deciding.
   - Yes: a new page at the top level with the typed title, and the link to it.

## For the test pass

(Added to `docs/test-pass.md` when the run is built.)

- On the phone: make a link with `[[`, and with the toolbar on selected text, with the on-screen keyboard open.
- On the laptop: `Ctrl+K` on selected text, link to a page; then `Ctrl+K` with nothing selected opens search.
- Paste a page's address from the address bar into another page: the new link shows under "Linked from" on that page after a few minutes.

## Answers (the recommended ones, given when B.1b was planned)

1. **`Ctrl+K` in the editor:** with text selected (or the caret in a link) it makes or changes a link; without, it opens search as everywhere.
2. **Where the page menu finds pages:** by title, in the tree this tab has.
3. **"Create page" when nothing matches:** not now.

## Handoff

Built on branch `feat/b1b-make-links`, on Sonnet 5.5 (PR #99); the review fixes on Opus 5.5.

**Done**
- **`[[` page menu** (`pageLinkExtension.ts`, `PageLinkMenu.vue`, `pageSearch.ts`): a TipTap `Suggestion` like the slash menu, over the tree's live pages (case and accents ignored, titles that start with it first, at most 8, never the page itself, the parent named when titles are shared). Enter or a click makes a link to `/p/<id>`. Escape keeps the text, `]` closes it, Enter with no match is left to the text.
- **"Link to page"** in the slash menu and the phone toolbar's **Link** button (no selection) type `[[` (`openPageMenu`): one way in.
- **Link picker** (`LinkPicker.vue`, a dialog in the style of search) for selected text: the same pages, and a typed web address (`http`, `https`, `mailto`) first; a page of this app typed in full becomes `/p/<id>`. It can also change a link and take it away. **Ctrl+K** opens it with text selected or the caret in a link; otherwise search, as before. The phone toolbar's Link button opens it for a selection.
- **Link menu** under a link: "Change link" and "Remove link" next to "Open".
- **Normaliser** (`pageLinkNormalizer.ts`): a link this person makes to a page of this app, by paste, a typed address or the picker, is stored as `/p/<id>`. It reads only what a local transaction changed, never a change from another client.
- Shared helpers: `useMenuPlacement` (the slash menu's positioning, now shared), `changedRanges` (also used by the broken-link decorations), `selectedRange`.
- Both languages; 14 blocks in the slash menu now.
- **Tests:**
  - Vitest for the search, menu, picker, shortcut and registry, normaliser (including that a remote change is never rewritten), toolbar button, link menu, and the editor end to end.
  - Playwright `make-links.spec.ts` on desktop and both phones, and the accessibility states of the three new surfaces on all four projects.
- **Mutation checks** (each removed for one run, its test failing, then put back): the shell yielding `Ctrl+K`, switching the link off after a chosen link, the normaliser's rewrite and its remote guard, the end-of-link rule, the picker's Enter handler.

**Deviations from the plan**
- **The picker is a dialog,** like the search dialog, not a popover under the selection: the same pattern and focus handling, and room on a phone with the keyboard open. It is Reka's `Dialog` and `Listbox`, not `Combobox`.
- **`Ctrl+K` needed two things the plan did not name:**
  - The app shell listens in the capture phase, before any element, so it asks the editor first. The question lives in `linkShortcutRegistry.ts`, which imports nothing, so that TipTap stays out of the main bundle.
  - ProseMirror's selection follows the browser's by one event, so a key pressed right after the arrow keys saw the old one: `selectedRange` reads the browser's selection first.
- **The link mark is inclusive here** (autolink is on), so typing right after a link made it longer. After a link is chosen the link mark is switched off for what comes next.
- **The picker's Enter** chooses the first option when the highlight has not caught up with the list (or has lost the option); Reka's own handler would take a stale one or nothing.
- **Found by the tests, fixed here:** `PageEditor.refreshFocus` ran after the editor was destroyed (a blur just before leaving a page). The existing slash-menu e2e counted 13 blocks.
- Typing right after a link edits that link, so a link that is extended by typing is normalised as a whole.
- **A cost found by the full run, not by any test:** the picker is always mounted, and a watcher on its list searched the whole workspace on every change of the tree even while it was closed. On the shared e2e workspace (hundreds of pages) that was enough to make an existing iPhone spec fail in two full runs out of two, while it passed ten times alone and `main` passed the same run: the spec types straight after an Enter that ProseMirror applies late on iOS, and the extra work on the main thread widened the gap. The picker now works nothing out until it is open (a test counts the pages it looks at; it fails without the fix).

**Review fixes** (`/code-review` at high, on Opus 5.5; each mutation-checked):
- **The picker's text is kept as Yjs relative positions** (`place.ts`, moved out of the attachment upload, which keeps them the same way). It saved ProseMirror positions when it opened, and a change from another client while it was open (which replaces the blocks it touches) put the link on other text. Text deleted meanwhile gets no link; the focus still goes back.
- **One rule for the text a link command acts on** (`linkRange.ts`): the whole link when the caret or all of the selection is in one, so "Change link", "Remove link" and Ctrl+K never split a link in two; otherwise the selection; nothing in code (a code block or inline code, where a link cannot go). The picker and the Ctrl+K question both ask it, so they can no longer disagree. If they did, neither would take the key and the browser's own Ctrl+K would run.
- **No `[[` typed into code** by the toolbar's Link button or "Link to page", and the menu of pages does not open in inline code.
- **The picker's Enter** now compares the field with what the list was made for. The old flag stayed set after a key that left the text as it was, and Enter then took the first option instead of the one the person moved to.
- **A page that stops being editable while the picker is open** gets no link: the picker closes.
- **One menu at the caret** (`suggestionMenu.ts`): the slash menu and the menu of pages share their state, keys and combobox wiring, and `useChosenInView`. They were copies.
- **The titles are folded once per tree**, not on every key (thousands of imported pages).
- **The menus' spoken result counts go through `n()`** (Intl), the slash menu's too.
- Checked, not changed: a second Ctrl+K in the picker does not open search over it. The shell opens nothing over an open dialog, and a test now says the picker is one.
- The flaky `ImportEndpointTests` test seen in the full run is unrelated to this run: it is run B.1c in `docs/architecture.md`.
- **The full run after the fixes failed one spec once:** `editor-blocks.spec.ts` "closes with Escape and a space" on the iPhone. It passed 41 of 41 alone. The spec typed straight after an Enter, which ProseMirror applies late on iOS; under the load of a full run the late Enter split the typed address and started a line with `/`. The spec now waits for the new line (`newLine`) before it types. The CSP report next to the failure was WebKit's own `<style>` element (see `press` in the same spec), from the screenshot it takes on a failure.

**Left for later:** "Create page" in the page menu (question 3), mentions that follow a title, links to a heading.

**Not done:** the manual checks, in `docs/test-pass.md` under "B.1b".

