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

Runs B.2 to B.8 add their checks here.

## Phase 2

Runs 2.1 to 2.10 add their checks here.
