# Run B.3: Favorites, recently opened pages, and a light theme

**Model:** S·low in the roadmap; planned as S·med, because the run adds two tables with a migration and five endpoints with their authorisation next to the second palette. Planned on Opus 5.5 high; builds on Sonnet 5.5 medium. `/code-review` at medium on Sonnet before the PR merges (no sync, import or permission rules change). One PR, branch `feat/b3-favorites-theme`.

## Goal

A person marks the pages they use most as favorites and finds them at the top of the sidebar, and the start page lists the pages they opened last, on the laptop and the phone alike: what is starred or opened on one device is there on the other. The app is light when the system is light and dark when it is dark, readable in both, and the person can pick one for this device instead.

## Inputs

- `docs/architecture.md`: the B.3 row; "Core tables"; "Permission resolution"; `live_pages`; "Quality standards" (accessibility, contrast; "dark mode through the `dark` variant" under Styling, which this run changes).
- `docs/design/inkpact_slate/DESIGN.md`: the colour tokens (dark only), the type scale, touch targets. The designs are all dark; none shows favorites or a recent list.
- `docs/test-pass.md`: manual checks go there, not into this run.
- Code:
  - `web/src/style.css` (`@theme`, the one palette), `web/src/contrast.test.ts` (reads the tokens from `style.css` with a regular expression), `web/index.html`, `web/src/main.ts`;
  - `web/src/i18n.ts` (`detectLocale`/`applyLocale`: a per-device choice in `localStorage`, guarded for blocked storage) and `web/src/layout/UserMenu.vue` (the language radio group): the pattern for the theme choice;
  - `web/src/layout/SidebarContent.vue`, `web/src/tree/TreeRow.vue` (the row menu), `web/src/pages/StartPage.vue`, `web/src/pages/PageView.vue` (the header row with `SyncStatus`), `web/src/stores/tree.ts` (`pages`, `trash`, the change-then-notice pattern);
  - `server/src/Inkpact.Api/Tags/TagEndpoints.cs` and `Links/BacklinkEndpoints.cs` (policies, the shape of a list), `server/src/Inkpact.Data/InkpactDbContext.cs`, `server/tests/Inkpact.Api.Tests/Permissions/EndpointPolicyTests.cs`;
  - `web/e2e/a11y.spec.ts`, `web/e2e/helpers.ts`.

## What is there now (found while planning)

1. **One palette, dark.** `style.css` defines every colour once in `@theme`, from DESIGN.md, which has no light values. **No component uses a raw colour** (no hex, `white`, `black` or Tailwind palette class outside the tokens; only `bg-transparent`), so a light theme is a second set of token values and no component changes for it.
2. **The contrast test checks one palette:** `token()` takes the first `--color-<name>: #xxxxxx;` in `style.css`. It must learn to read two.
3. **The strict CSP has no room for a script in `index.html`.** A theme that follows the system must therefore come from CSS alone, or every light-system load would flash dark until `main.ts` runs. A theme chosen in the app can be set by `main.ts` before the app mounts.
4. **Nothing records favorites or opened pages.** The start page says "pick a page" and offers a new page.
5. **The tree store has every live page of the workspace** (title, parent) and the trash. A list of page ids can be shown with titles from it, so a rename shows at once and needs nothing here.
6. **The language choice is per device** (`localStorage`, `inkpact.locale`), set in the user menu. Architecture says "a per-user override in settings"; what was built is per device.

## Decisions

1. **The tokens hold both themes, with `light-dark()`.** Each colour token in `@theme` becomes `--color-<name>: light-dark(<light>, <dark>);`, and `:root` gets `color-scheme: light dark`. The browser then picks by the system setting with no script and no flash, native controls and scrollbars follow, and every Tailwind class (opacity modifiers too) keeps working. `:root[data-theme='light'] { color-scheme: light }` and `:root[data-theme='dark'] { color-scheme: dark }` make a choice in the app win. Supported by every browser of the Playwright projects and by iOS Safari from 17.5; checked while planning in Chromium and WebKit, with the system scheme, with `data-theme` against it, and through `color-mix()` (what `bg-surface-canvas/70` and `bg-primary-container/30` compile to). No `dark:` classes anywhere.
2. **The light values,** checked while planning against every pair the contrast test asks for (the lowest ratio over the six surfaces is in the comment). The dark values stay as they are. The presence colours stay one value (phase 2).

   ```text
   surface #fcf8ff                surface-dim #dcd8e5             surface-bright #fcf8ff
   surface-container-lowest #ffffff  surface-container-low #f6f2fe  surface-container #f0ecf9
   surface-container-high #eae6f3    surface-container-highest #e4e1ee
   on-surface #1b1b24 (13.9)      on-surface-variant #464555 (7.6)
   inverse-surface #302f39        inverse-on-surface #f3effc
   outline #636176 (4.9)          outline-variant #c7c4d8         surface-tint #4d44e3
   primary #4338ca (6.4)          on-primary #ffffff
   primary-container #4f46e5      on-primary-container #ffffff (6.3)   inverse-primary #c3c0ff
   secondary #00628b (5.5)        on-secondary #ffffff
   secondary-container #c9e6ff    on-secondary-container #001e2f
   tertiary #953a00               on-tertiary #ffffff
   tertiary-container #ffdbcc     on-tertiary-container #351000
   error #b3261e (5.3)            on-error #ffffff
   error-container #ffdad6        on-error-container #410002 (13.3)
   background #fcf8ff             on-background #1b1b24           surface-variant #e4e1ee
   surface-canvas #ffffff         surface-panel #f7f7fa
   surface-subtle #f1f2f6         surface-elevated #eceef3
   border-subtle #e1e3ea          border-strong #c3c7d2
   sync-active #03704f (5.0)      sync-offline #a14a06 (4.9)
   ```

   In the light theme `surface-elevated` is darker than the panel (a hover or a menu shows as a grey), the opposite of dark, which is what the token means in both. The build checks the screens by eye in both themes, and a value may change where something reads badly, as long as the contrast test passes.
3. **The theme choice** (question 1): "Theme: System, Light, Dark" in the user menu, a radio group under the language's, per device in `localStorage` (`inkpact.theme`, guarded as `i18n.ts` is), System by default. `theme.ts` reads it and sets `data-theme` on `<html>` in `main.ts` before the app mounts (none for System). `index.html` gets `<meta name="color-scheme" content="light dark">` and a `theme-color` meta per scheme (`surface-panel` of each), static, so the phone's browser bar matches.
4. **The contrast test checks both palettes.** `token()` reads `light-dark(a, b)` and every check runs once for light and once for dark. A new test fails when a colour token has only one value (the presence colours are the listed exception).
5. **Favorites** (question 2):
   - **Table `favorites`:** user, page, created at; key (user, page); both foreign keys cascade. Private to the user: nobody else reads them, not even the workspace's owner.
   - **`PUT /api/pages/{id}/favorite`** and **`DELETE /api/pages/{id}/favorite`**, `RequirePageRole(Viewer)` (a viewer may star a page), both idempotent, 204. A page in the trash may be unstarred but not starred (`page_in_trash`, 409). At most 200 per user and workspace (`too_many_favorites`, 409).
   - **`GET /api/workspaces/{id}/favorites`**, `RequireWorkspaceRole(Viewer)`: the ids of the user's favorite live pages in that workspace (`live_pages`), in the order they were starred. A page in the trash is left out and kept, so it is back when the page is restored.
6. **Recently opened pages** (question 2):
   - **Table `page_visits`:** user, page, opened at; key (user, page); both foreign keys cascade; an index on (user, opened at).
   - **`POST /api/pages/{id}/visit`**, `RequirePageRole(Viewer)`: sets opened at to now (an upsert, `ON CONFLICT` through EF's `ExecuteUpdate`, or a find-then-insert retried once on the unique key, as the tags were) and deletes the user's visits beyond the 50 newest in that workspace, in one transaction. 204. Sent by `PageView` once when a page opens (not on a reload of its tags or backlinks); a failure is ignored, never a notice.
   - **`GET /api/workspaces/{id}/recent`**, `RequireWorkspaceRole(Viewer)`: `[{ pageId, openedAt }]` for live pages, newest first, at most 50.
   - Neither table touches the page's `version`, `updated_at` or the tree version, and neither writes an audit line.
7. **Where they show** (question 3):
   - **The sidebar:** a "Favorites" list above the tree (titles from the tree store, `ListedPage`'s look without the parent), each opening its page; not there when there are none. In the phone's drawer the same.
   - **The page:** a star button in the header row before `SyncStatus` ("Add to favorites" / "Remove from favorites", `aria-pressed`), on every page the person can open. The tree row's menu gets the same item.
   - **The start page:** "Recently opened", the 10 newest, each with its parent's title in small type and when it was opened (`Intl.RelativeTimeFormat`, "5 minutes ago"); under it the new-page button as now. Empty: the current text.
8. **The stores:** `stores/favorites.ts` (ids, `toggle` that changes the list at once and puts it back with a notice when the server refuses) and `stores/recent.ts` (read when the start page opens). Both read again when the tab gets focus, as the backlinks panel does, so a star from the phone is on the laptop when it comes back to it. An id the tree store does not have (a page made on another device since) is left out until the tree is read again.
9. **Phase 2:** the lists filter by the workspace role only. When page shares come (run 2.3), the list queries must go through the permission service. Written into the architecture's permission section, so 2.3 finds it.
10. **i18n and accessibility:** every label and error code in English and Dutch. The favorites list is a `nav` with a label; the star is a toggle button with a label that does not change and `aria-pressed`; the recent list is a list with a heading. `a11y.spec.ts` scans its states in both themes (axe checks the contrast of what it renders, so the light palette is checked on real screens too).

## Done when

- With the system set to light the app is light, and dark when dark, from the first paint and with no flash; Light or Dark in the user menu wins on that device and stays after a reload.
- Every screen is readable in both themes: the contrast test passes for both palettes, and axe is clean on every scanned state in both themes on every project.
- A page can be starred from its header and from its row menu, with the keyboard and by touch; it is at the top of the sidebar, also after a reload and on another device; unstarring takes it away.
- The start page lists the pages opened last, newest first, with when; a page opened on the phone is listed on the laptop.
- A page in the trash is in neither list, and is back in both when restored; a page deleted for good is gone from them.
- Another user never sees someone's favorites or visits; the deny-by-default test covers the five endpoints.
- Both languages, CSP clean.

## Tests

- **Server:**
  - favorites: star and unstar are idempotent; a viewer may star; a page in the trash cannot be starred but can be unstarred; the 200 limit; the list has live pages only, in starring order, back after a restore; another user's favorites are not in it; a page of another workspace is `not_found` as every page endpoint;
  - visits: an open sets the time, a second open moves the page to the front; the 51st page drops the oldest; the list is live pages only, newest first; another user's visits are not in it; parallel opens of one page by one user end with one row;
  - a page deleted for good takes its favorite and visit rows with it (the trash purge);
  - `EndpointPolicyTests` covers the five new endpoints; an anonymous request is refused;
  - the migration.
- **Vitest:**
  - `theme.ts`: reads and stores the choice, survives blocked storage, sets and removes `data-theme`; the user menu's theme radio;
  - `contrast.test.ts`: every check for both palettes; every colour token has a light and a dark value;
  - the favorites store (toggle at once, rollback and notice on refusal, reload on focus), the star button, the sidebar list (empty, titles from the tree, a missing id left out), the row menu item;
  - the recent store and the start page list (order, parent title, relative time in both languages, empty).
- **Playwright:**
  - `favorites.spec.ts` (desktop and both phones): star a page from its header, see it in the sidebar (the drawer on a phone), reload, unstar from the row menu; open two pages, go to the start page, both listed, the last first; trash one, it is gone from both lists, Undo, it is back;
  - `theme.spec.ts`: `page.emulateMedia({ colorScheme })` light and dark gives the canvas colour of each; Light in the menu with a dark system stays light after a reload; System follows the system again;
  - `a11y.spec.ts`: the existing states in both themes, the sidebar with favorites and the start page with recents, all four projects; CSP clean.

## Docs to update

- `docs/architecture.md`: "Core tables" (`favorites`, `page_visits`); "Permission resolution" (decision 9); Styling under "Quality standards" (`light-dark()` tokens and a per-device choice, not the `dark` variant); the language line ("per device", as built).
- `docs/design/inkpact_slate/DESIGN.md`: the light value of each colour token next to the dark one.
- `docs/test-pass.md`: B.3's checks.

## Out of scope

- Reordering favorites, folders of favorites, favorites shared with others.
- Clearing the recent list, or recents in the sidebar; recents and favorites in the quick switcher (B.4).
- A theme stored on the server per user, a high-contrast theme, themes for code blocks.
- The PWA manifest's colours (B.6).

## Questions

1. **The theme:**
   - **Follow the system, with System, Light and Dark in the user menu, per device. Recommended:** a laptop in daylight with a dark system, or a phone at night, is when the choice matters, and it is the same small pattern as the language.
   - Only the system setting, no choice in the app.
2. **Where favorites and opened pages are kept:**
   - **On the server, per user: the phone and the laptop share them. Recommended:** daily use is on both, and the gate is "nothing makes you miss Joplin". Two tables and five endpoints.
   - In the browser, per device (`localStorage`): no server change, the run becomes S·low, but the phone and the laptop each have their own lists.
3. **Where they show:**
   - **Favorites at the top of the sidebar, a star on the page and in the row menu; the recent list on the start page. Recommended:** the sidebar is where pages are found on both devices, and the start page is empty now.
   - Both lists in the sidebar, above the tree.

## Answers (the recommended ones, given when B.3 was planned)

1. **The theme:** follow the system, with System, Light and Dark in the user menu, per device.
2. **Where they are kept:** on the server, per user, shared by the phone and the laptop.
3. **Where they show:** favorites at the top of the sidebar, a star on the page and in the row menu; the recent list on the start page.

## For the test pass

(Added to `docs/test-pass.md` when the run is built.)

- On the phone, switch the system between light and dark: Inkpact follows, also in the browser's bar; on the laptop, choose Light in the user menu with a dark system, reload: still light.
- Read a long note in the light theme in daylight on the phone, and in the dark theme at night: both comfortable; the editor's toolbar, menus, notices and the import report readable in both.
- Star a page on the laptop: it is at the top of the sidebar on the phone. Open a page on the phone: it is first under "Recently opened" on the laptop.
- With TalkBack (phone) or a screen reader (laptop): the star is announced as a toggle with its state, and the recent list with its heading.
