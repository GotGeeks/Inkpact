# Manual test pass

Hands-on testing is done once, just before the first public release (run 2.10b in `docs/architecture.md`), not per run or per phase. This was decided on 2026-11-07. Until then, implementation goes on, and Joplin stays the main app.

This file collects everything that needs a person, a phone or the Unraid box. Every run adds the checks it cannot automate here, under its own run number, and keeps them in its card's "For the test pass" section (the older cards call it "For the trial"). A check that fails becomes an issue and a bug-fix run before 2.11.

**Setup:** the app on the Unraid box behind Traefik on its subdomain, the laptop, and an Android phone. A real Joplin export is needed for the import checks.

## The gates

- [ ] **Phase 1A:** you use it daily on phone and laptop next to Joplin for two weeks, re-importing as needed, and no note is lost or broken. Then Joplin is retired.
- [ ] **Phase 1B:** nothing in daily use makes you miss Joplin.
- [ ] **Phase 2:** a second person in your household uses a shared workspace without help.

## Phase 1A

### 1.13: running on Unraid
- [ ] The instance runs on Unraid behind Traefik on the test subdomain. You sign in, create a page, and edit it from a phone.

### 1.14: attachments
- [ ] A photo from the phone's camera roll, added from the toolbar, shows on the laptop.
- [ ] A large PDF downloads through Traefik.
- [ ] After `docker compose pull && up -d`, earlier attachments are still there.

### 1.15 and 1.16: the Joplin import
- [ ] Your real Joplin export imports, and the report's list of imperfect conversions makes sense.
- [ ] A few notes with images, tables and checklists look right on the phone and on the laptop.
- [ ] Re-import: change a note in Joplin, one in Inkpact and one in both. Export, import again, and check the three outcomes and the report.
- [ ] A note open on the phone updates while you watch during a re-import.
- [ ] Links between your notes work on the phone and the laptop.
- [ ] Your largest note opens, and the large-page notice makes sense.

### 1.17: search
- [ ] Search for a word you know is in an old Joplin note, with and without its accents, and half typed.
- [ ] Search on the phone: open, type, open a result, and the keyboard does not cover the results.
- [ ] Type a new word into a page, wait two minutes, and search for it.

### 1.18: export
- [ ] Export, open the zip on the laptop, and open a few notes in a text editor and in Obsidian (or Joplin's "Import Markdown"): pictures show, links between notes work, nested notebooks are folders.
- [ ] Export on the phone: the download starts and ends.
- [ ] Behind Traefik, export a large library and check that it arrives whole (the zip opens). The proxy must not buffer the response or time it out.

### 1.19: responsive and accessible
- [ ] With TalkBack on the phone: open a page from the drawer and hear its title; open search and hear the results.
- [ ] With the phone's text size at its largest: the drawer, a page, search and the import report still fit without scrolling sideways.
- [ ] Keyboard only on the laptop, for a while of real use: anything the automated walk did not cover.

### 1.21: offline edits on a large page
- [ ] Behind Traefik, on the laptop, open an imported note of several MB. Go offline in DevTools and type at the end. On the phone, delete that paragraph. Bring the laptop back online. A copy appears beside the note and holds your text. This also checks that Traefik lets a request of about 13 MB through.
- [ ] The same with the phone as the offline side (airplane mode), on a small note.

### 1.22: a changed large note
- [ ] Change a note of several MB (up to about 5 MB of text) in Joplin, export, and import again. The report says it was updated (no file), and the page opens at once on the phone and on the laptop with the new text.

## Phase 1B

### B.1a: backlinks and broken links
- [ ] With your real library: a note you know other notes link to shows them under "Linked from", on the laptop and on the phone. (Right after the update, give it a minute: the index is made when the server starts.)
- [ ] Trash a page that others link to: their links to it are struck through, and "Linked from" on it still lists what links to it. Restore it: the links are whole again.
- [ ] Open a note with a broken link with a screen reader (TalkBack): the reason is read after the link.

### B.1b: making links
- [ ] On the phone, with the on-screen keyboard open: type `[[` in a note and pick a page; the link opens that page. Then select a word, tap the Link button in the toolbar and link it to a page; then tap the Link button with nothing selected: the menu of pages opens.
- [ ] On the laptop: select a word and press Ctrl+K, link it to a page; press Ctrl+K again with nothing selected: search opens.
- [ ] Link a word to a web address with Ctrl+K, then change it and remove it from the menu under the link.
- [ ] Paste a page's address from the address bar into another page: after a few minutes the other page lists it under "Linked from".
- [ ] With TalkBack (phone) or a screen reader (laptop): the menu of pages and the picker are announced, with the number of results and the option that is chosen.

### B.2: tags in the interface
- [ ] With your real library: a note you tagged in Joplin shows its tags, on the laptop and on the phone; the "Tags" view in the sidebar lists them with sensible counts.
- [ ] On the phone, with the on-screen keyboard open: add an existing tag and a new one to a note, remove one.
- [ ] Rename a tag from its own page, remove a tag from a note, then import the same Joplin export again: both changes are still there.
- [ ] With TalkBack (phone) or a screen reader (laptop): the tag row and the add-tag field are announced, with the number of suggestions.

### B.3: favorites, recently opened pages and the light theme
- [ ] On the phone, switch the system between light and dark: Inkpact follows, also in the browser's bar; on the laptop, choose Light in the user menu with a dark system, reload: still light.
- [ ] Read a long note in the light theme in daylight on the phone, and in the dark theme at night: both comfortable; the editor's toolbar, menus, notices and the import report readable in both.
- [ ] Star a page on the laptop: it is at the top of the sidebar on the phone. Open a page on the phone: it is first under "Recently opened" on the laptop.
- [ ] With TalkBack (phone) or a screen reader (laptop): the star is announced as a toggle with its state, and the recent list with its heading.

### B.4: quick switcher, command palette and keyboard shortcuts
- [ ] On the phone, open the palette with the on-screen keyboard up; switch between pages, tags and commands, scroll the results, open a page and a tag, and check that the keyboard does not cover the chosen item.
- [ ] Star and open pages on one device, then open the palette on the other and clear the Pages field: Favorites and Recent are current, do not repeat a page, and no trashed page is offered.
- [ ] On the laptop, check Ctrl+K in the editor with and without a selection and with the caret in a link; try the command and new-page shortcuts, including in an input and with a dialog open. Check a non-Latin or AltGraph layout (US-International: Ctrl+Alt+N outside text makes a page, AltGr+N in text still types "ñ") and Cmd/Option on a Mac if available.
- [ ] With TalkBack on the phone and a screen reader on the laptop, hear the active scope, results, errors and shortcut help; move through the list and dismiss it back to the opener, or the menu button when the drawer was closed.
- [ ] With the real Joplin library, search and switch pages quickly, and type in the editor with the palette closed: neither action should feel held up by the palette.

### B.5: version history, restore and retention
_Built in run B.5b: capture (PR 1), the panel (PR 2), the protection of typing during a replacement (PR 3), restore (PRs 4 and 5) and the settings screen (PR 5). On restore: when the page's newest version already shows the page as it is, that version is protected and becomes the safety version, so the history shows no copy of it labelled "Before a restore"._
- [ ] On the laptop, change a page's title and content, wait for a version, preview and restore an earlier one, then restore the before-restore version. Check a picture and an internal link, and confirm tags and the page's place did not change.
- [ ] Open the same page on the phone and laptop. Edit it on the phone with the tab kept open and offline, restore an older version on the laptop, then reconnect the phone: the offline text remains in the page or an announced copy. Also try restoring while the other device is editing.
- [ ] In a spare test workspace, change the history windows and cap, turn history off and on, reload, and check the warning and availability of older versions. A non-owner cannot change the setting; do not use the real library to test destructive limits.
- [ ] On the phone and with TalkBack or a laptop screen reader, open the history list, preview, confirmation and settings; check labels, focus, scrolling, both themes and returning to the editor.
- [ ] After the upgrade that brings the capture (B.5b, PR 1), leave the real library alone for an afternoon and check that pages get their first version (`select count(distinct page_id) from page_versions` grows towards the number of pages), that typing stays smooth meanwhile, and that the app's log names no page as unreadable.
- [ ] Run a Joplin re-import of a changed note while typing in that note on the phone: the typed text is in the page or in an announced copy (B.5b, PR 3).

### B.6: installable PWA and offline Yjs persistence
- [ ] On the laptop over HTTPS, install Inkpact if the browser offers it. Open a page online, disconnect the network, edit and reload the same page URL; confirm the text survives with an honest local-only/offline status. Reconnect and confirm a second client receives the edit once.
- [ ] Repeat on an Android phone. On an iPhone, add the app to the Home Screen manually, then check offline reload and reconnect; do not expect the same install prompt as Chromium.
- [ ] Open a page never loaded on the device while offline and confirm it is reported unavailable, not shown as empty. Check that tree mutations and attachment uploads are unavailable offline.
- [ ] With an offline edit pending, make a newer app version available. Confirm the open editor is not forced to reload and the edit is not discarded; update after the pending change is safe.
- [ ] With nothing pending and Inkpact open in one tab (and in the installed phone app), make a newer version available and choose **Update now**: the app reloads in the new version. With a second tab open it asks to close that tab first.
- [ ] Sign out on a shared laptop profile, then check the browser's site data (or reopen a cached page URL offline): synced pages of that account are gone; a page with an edit that had not reached the server is still there for that account only.
- [ ] Restart the server or switch the phone between Wi-Fi and mobile data while a page is open: the sidebar, header and title stay; only the sync status changes. Typing in a large imported note stays smooth with the network on and off.
- [ ] In a spare account/profile, sign out and sign in as another account. Confirm the prior account's cached notes and pending edits are not visible or sent as the new account.
- [ ] In a disposable multi-user workspace when available, downgrade or revoke a user's access while that user has a pending offline edit. On reconnect, confirm the edit stays on the device and is not sent; restore edit access and check that the original account can recover it. Also confirm a viewer can read a previously confirmed page but cannot edit it.
- [ ] If using an HTTP LAN install, confirm it still works online and does not claim PWA installation or service-worker caching support.

### B.7: health, logs, backup and restore, password reset
- [ ] On Unraid, schedule the nightly backup with User Scripts (the script in the self-hosting guide), let it run, then restore it into a spare stack within an hour: notes, tags, attachments and sign-in are there.
- [ ] Stop Postgres while the app runs: Unraid's Docker tab shows Inkpact unhealthy, and healthy again after Postgres is back. Upgrade across a migration with the real library: the dump before it does not mark the container unhealthy.
- [ ] Reset the password from the container console, sign in with it, and check that the other signed-in browser (the phone) is signed out within five minutes.
- [ ] Read the app's log in Unraid's log viewer: one line per entry, nothing from a note in it.
- [ ] With a PostgreSQL you already run: `deploy/inkpact.sh backup`, then `restore` into an emptied database. These two commands were not run by CI or while building (only their syntax was checked).

### B.8: releases, the Unraid template and the update check
_PRs 1 (the version and the update check) and 2 (the release pipeline) are built; the Unraid template (PR 3) follows. The first release is cut once the release App is set up (CONTRIBUTING, "Releases")._
- [ ] Signed in as the administrator with an older version running, the account menu says a newer one is out, and the settings screen links to its notes. As another account (from phase 2), neither says so.
- [ ] With `UpdateCheck__Enabled=false`, the container makes no request to `api.github.com` (the router's or Pi-hole's log).
- [ ] Install Inkpact on Unraid from the template's URL with a PostgreSQL 18 container: the `chown`, the connection string, the setup code. The Docker tab shows it healthy, and it works behind Traefik.
- [ ] After the next release, Unraid's "Check for updates" offers it; updating keeps notes, attachments and sign-in, and a dump appears in `/data/backups` when the release has a migration.
- [ ] `cosign verify` of the installed tag on the laptop, with the guide's command.
- [ ] At 2.11: switch `UNRAID_CA_SYNC` on, merge the first sync PR in the CA repository, and find Inkpact in Community Apps.
### The review of 2026-11-11
- [ ] With the device in dark mode, start the installed app (laptop and phone): note whether the title bar and the first screen are light until the page has loaded (the manifest has one theme colour, the light one). If it flashes, that is a finding for a fix run.
- [ ] Read the whole interface in Dutch once. 13 strings are the same in English and Dutch; most are names (Inkpact, Joplin), but note any that should have been translated.
- [ ] If the install sits behind two proxies (a tunnel in front of Traefik), set `Network__ForwardLimit` to `2`, make a wrong login from two networks and check that the audit log shows two different addresses, neither of them the tunnel's.
- [ ] On an Android phone, look at the installed app's icon: without a maskable icon Android crops the plain one into a circle. Note whether it looks cut off.

Runs B.7 and B.8 add their checks here.

## Phase 2

Runs 2.1 to 2.10 add their checks here.
