# Run B.9: Account settings

**Model:** O·high → S·med in the roadmap; kept. Planned on Opus 5.5 on 2026-11-13; builds on Sonnet 5.5 medium. Two PRs. PR 2 changes a password and ends sessions, an authentication path, so it gets `/code-review` at **high**; PR 1 at medium.

**Status:** Planned; PR 1 in progress. The maintainer answered the three questions on 2026-11-13, each as recommended: no email change in this run, a password change always signs out the other devices, no addresses in the device list.

## Goal

A person manages their own account in the app:
- they can change their display name;
- they can see which devices are signed in and sign one out (a lost phone, a borrowed laptop);
- they can change their password, with the current one asked and every other device signed out.

Until now the only way to change a password was the command line (run B.7). The session table already keeps when each device signed in, when it was last seen and its browser, and nothing reads that.

## Inputs

- [`docs/architecture.md`](../architecture.md): the B.9 row; "Security from day one" (sessions in Postgres, "revoking one session takes effect on the next request", passwords, login throttling, CSRF, the audit log, WebSocket hardening: "logout and logout everywhere close the session's sockets at once").
- [`docs/review-2026-11-11.md`](../review-2026-11-11.md): finding 1.2 (no way to change a password, the display name or the email, or to see or end a session) and 5.6 (a settings screen rather than a longer menu).
- [`docs/runs/B.5b.md`](B.5b.md): PR 5 made `/settings` and left room for this run. [`B.7.md`](B.7.md): `reset-password` (what it ends, the audit event without an address). [`B.6.md`](B.6.md): the offline copy, and what stays on a device after sign-out.
- Code:
  - [`AuthEndpoints.cs`](../../server/src/Inkpact.Api/Auth/AuthEndpoints.cs): sign-in (the user agent kept with the session), logout, logout everywhere, `LoadUser`.
  - [`PostgresTicketStore.cs`](../../server/src/Inkpact.Api/Auth/PostgresTicketStore.cs) and [`Session.cs`](../../server/src/Inkpact.Data/Entities/Session.cs): the session row, whose **id is the secret the cookie holds**, `SessionKeyItem` (a request knows its own session), `RemoveAsync` and `RemoveAllForUserAsync`.
  - [`SessionEvents.cs`](../../server/src/Inkpact.Api/Sync/SessionEvents.cs): `SessionEnded(key)` and `AllSessionsEnded(userId)` close sync sockets at once.
  - [`PasswordHashing.cs`](../../server/src/Inkpact.Api/Auth/PasswordHashing.cs) (`PasswordPolicy`, rehash), [`LoginThrottling.cs`](../../server/src/Inkpact.Api/Auth/LoginThrottling.cs), [`AuditLog.cs`](../../server/src/Inkpact.Api/Auth/AuditLog.cs) and `AuditEventTypes`.
  - [`ResetPasswordCommand.cs`](../../server/src/Inkpact.Api/Auth/ResetPasswordCommand.cs): the transaction that writes a hash, removes sessions and writes the audit event, to follow.
  - [`SettingsPage.vue`](../../web/src/settings/SettingsPage.vue), [`AboutSection.vue`](../../web/src/settings/AboutSection.vue), [`UserMenu.vue`](../../web/src/layout/UserMenu.vue) (sign-out clears the offline copy), [`stores/auth.ts`](../../web/src/stores/auth.ts), [`SetupPage.vue`](../../web/src/pages/SetupPage.vue) (the password field and its hint).
  - [`EndpointPolicyTests.cs`](../../server/tests/Inkpact.Api.Tests/Permissions/EndpointPolicyTests.cs), `LogContentTests`.

## What is there now (found while planning)

1. **Sessions:** one row per signed-in browser:
   - its id (32 random bytes, which **the cookie holds**: whoever has it is signed in);
   - the user, created, last seen (updated at most once a minute), expires (30 days, sliding), and the user agent (at most 256 characters).
   - No address is kept, and nothing reads the rows but the cookie handler and the cleanup job.
2. **Ending sessions:** logout removes the current row, and logout everywhere removes all of the user's rows. Either way the hub is told, and it closes the matching sync sockets at once (close code 4401). The client then goes to the login page; a device with unsent edits keeps them and says so (run B.6).
3. **Sign-out and the offline copy:** sign-out from the menu clears the device's offline copy of that account. A session ended from elsewhere does not clear it: the device goes to the login page, and its saved pages stay until someone signs in there with another account, or the browser's data is cleared. That is B.6's design, because a device can hold edits that were never sent.
4. **No account endpoints:** `GET /api/auth/state` gives the user (id, email, display name, admin). Nothing changes them; the setup page sets the display name once.
5. **Settings:** `/settings` has the workspace's history and "About Inkpact". It is reached from the account menu, which shows it only when a workspace is loaded.

## Decisions

1. **Endpoints, under `/api/account`, all for the signed-in person only** (`Policies.SignedIn`, CSRF as every write):
   - `PATCH /api/account` with `{ displayName }`: 1 to 200 characters after trimming (the setup rule). Answers the user as `/api/auth/state` gives it.
   - `GET /api/account/sessions`: the person's sessions that have not expired, the current one first, then by last seen. Each has `{ id, signedInAt, lastSeenAt, userAgent, current }`. At most 100, with `more` for how many are left out.
   - `DELETE /api/account/sessions/{id}`: signs that device out (204). For the current session it answers `409 session_is_current`: the client signs out through the menu's own path, which also clears the offline copy.
   - `POST /api/account/sessions/sign-out-others`: every session but the current one. Answers how many ended.
   - `POST /api/account/password` with `{ currentPassword, newPassword }` (PR 2, decision 4).
2. **A session's handle is not its key.** The list never sends a session id, which would sign in whoever reads it. Its `id` is a handle: the first 16 bytes of SHA-256 over the session id, base64url. It is worked out from the key and cannot be turned back into it, so no migration and no new column are needed. `DELETE` finds the session among the person's own rows by that handle; someone else's handle, or one that does not exist, is `404 session_not_found`, the same answer.
3. **Ending sessions closes their sockets at once:**
   - **One device:** `RemoveAsync`, which already tells the hub.
   - **All the others:** one statement removes every row of the user but the current one. A new `SessionEvents.AllSessionsEndedExcept(userId, keepKey)` closes their sockets without closing this one, and a session made between a read and the delete is covered too.
   - **Audit:** `session_revoked` (one device) and `sessions_revoked` (all the others, with how many), with the address and user agent as every event.
4. **Changing the password** (PR 2):
   - **Throttled as a login is,** because a stolen session must not be a way to guess the password: the per-address limit (10 a minute) and the lockout per email, the same counter as login. A wrong current password counts as a failed login for that address. While locked, the change is refused with `429 login_locked` and `Retry-After`, and the hash is still worked out, so the answer takes as long.
   - **The new password follows `PasswordPolicy`** (12 to 128 characters, not the email), with its error codes. It may not be the current one: `password_unchanged`, because a change that changes nothing would only sign the other devices out.
   - **In one transaction:** the new hash (Argon2id, worked out before the transaction opens), every other session of the user removed, the lockout counter cleared, and an audit event `password_changed`. A wrong current password writes `password_change_failed` and changes nothing else.
   - **After the commit:** `AllSessionsEndedExcept` closes the other devices' sockets. The current session stays signed in, and its ticket needs no change, since it holds only the user id.
   - **Every other device is signed out, always,** as the roadmap says (question 2).
5. **Display names in logs:** never. A display name is something a person typed, and gets a marker in `LogContentTests`, as the password flow does.
6. **The client: an "Account" section at the top of `/settings`,** before the workspace's history.
   - **Display name:** a field and Save; the account menu shows the new name at once (the auth store's user).
   - **Signed-in devices:** each row has a name worked out from the user agent ("Firefox on Windows"), when it signed in, and when it was last active (`Intl.RelativeTimeFormat`). The current device says "This device" and has no button; the menu signs it out. The others have "Sign out" behind a confirmation in the row, as the history panel does it. "Sign out all other devices" sits under the list.
   - **The device name:** a small parser of our own (`web/src/settings/deviceName.ts`), no dependency. It knows Edge, Chrome, Firefox, Safari, Samsung Internet and Opera, on Windows, macOS, iOS, iPadOS, Android, ChromeOS and Linux, and falls back to "Unknown browser". The full user agent is in the row's `title`.
   - **Change password** (PR 2): current password, new password (one field with the setup page's hint, as setup has), and Save. It says what is wrong per field. After a change it says "Password changed. Your other devices are signed out." and clears the fields.
   - **The account menu** shows Settings also when no workspace is loaded, since the account section does not need one.
   - **The note under the device list:** signing a device out ends its session, but pages it kept for offline use stay on it until it is used to sign in to another account. A lost device is safest with its own lock.
7. **No email change** (question 1), **no addresses in the list** (question 3), and no new table or migration.
8. **No new dependency** (npm or NuGet).

## The PRs

| PR | What | Size | `/code-review` |
| --- | --- | --- | --- |
| 1 | The account section: display name, the device list, signing out one device or all the others (server, client, both languages, e2e) | Medium | medium |
| 2 | Changing the password (server, client, both languages, e2e) | Medium | **high** |

## Done when

- **Display name:** a person can change theirs, and every screen that shows it (the menu, a version's author) shows the new one.
- **The device list:** it shows each signed-in browser with a readable name, when it signed in and when it was last active, and which one is this. It sends no session id, and one person cannot see or end another's sessions.
- **Signing out:** signing out one device, or all the others, ends those sessions on their next request and closes their sync sockets at once. The current device stays signed in.
- **Changing the password:** it needs the current password. A wrong one counts toward the login lockout, and a locked account cannot change it. The new password follows the same rules as setup. Every other device is signed out at once, the current one stays, and both outcomes are in the audit log.
- **Logs:** no log line holds a display name or a password.

## Tests

- **Server, real Postgres:**
  - **Display name:** the rules (empty, spaces only, 201 characters, trimmed); the answer; `/api/auth/state` after.
  - **Sessions:**
    - the list is the person's own, unexpired, current first, with handles that are not keys (no session id in the body) and a cap of 100 with `more`;
    - `DELETE` by handle: the other device's next request is 401, and its open socket closes with 4401;
    - someone else's handle or a made-up one gives 404, and nothing changes;
    - the current one gives 409;
    - `sign-out-others`: the current session stays and its socket stays open, the others' close, and a third user's are untouched;
    - the audit rows.
  - **Password (PR 2):**
    - right current password: the new one signs in, the old one does not, the other sessions and their sockets end, this one stays, the lockout is cleared, the audit row is written;
    - wrong current password: 401 `current_password_invalid`, the failure is counted, five lock the email (also for login), and nothing else changes;
    - locked: 429 with `Retry-After`, and the hash is still worked out;
    - the policy codes and `password_unchanged`;
    - a failure in the transaction changes nothing.
  - **Every new endpoint:** in the endpoint policy test (signed in only), with CSRF required and anonymous refused.
  - **`LogContentTests`:** markers in a display name, and in the current and new passwords.
- **Web, Vitest:**
  - `deviceName.ts`: a table of real user agents, current ones and old ones, including the installed app's;
  - the account section: saving the name, errors, the list, the confirmation, signing one out and all the others, the current device, the password form and its errors (PR 2), both languages.
- **Playwright** (the e2e account is shared by every device project, so nothing here changes its password or its name for good):
  - **The device list, real:** a second session is made by signing in with its own request context; the list shows two; "Sign out" ends it, and that context's next request is 401. It is safe to run in parallel, since it touches only its own session.
  - **The name and the password:** answered with `page.route`; their real effect is in the server tests.
  - **a11y:** the section, its confirmation and its errors are scanned.
- **While building:** the server test classes you change, `vitest related`, the settings spec. Before each push `pnpm check:push`; before the last push of each PR, `pnpm check` once with Postgres up.

## Docs to update

- [`docs/architecture.md`](../architecture.md), in the same PR:
  - "Security from day one": the sessions line (a person ends their own sessions from the settings screen);
  - the passwords line (changed from the settings screen with the current one, throttled as a login);
  - the audit log's event list.
- [`docs/self-hosting.md`](../self-hosting.md): "A forgotten password" says that someone who knows theirs changes it under Settings, and that the command line is for a forgotten one.
- [`docs/test-pass.md`](../test-pass.md): the checks below, under "B.9".
- This card: a handoff per PR.

## Out of scope

- **Changing the email** (question 1): it is the sign-in name, and with no email sent yet a typo cannot be caught. It comes with email in run 2.10, with a confirmation link.
- **Removing one's own account, two-factor sign-in, passkeys, and an administrator managing other people's accounts** (run 2.9, the admin page).
- **Wiping a device's offline copy from elsewhere:** the device could hold edits that were never sent (B.6). The note under the list says what stays.
- **Notifying a person by email of a new sign-in or a changed password** (run 2.10).

## Questions

1. **Can a person change their email here?**
   - **Recommended:** not in this run. It is the sign-in name. Inkpact sends no email yet, so a typo would lock the person out with nothing to catch it (only the administrator's command line). Run 2.10 brings email, with a confirmation link.
   - **The other way:** allow it now, asking for the current password, with the risk of a typo.
2. **Does changing the password always sign out the other devices?**
   - **Recommended:** yes, as the roadmap says. A password is changed because it may be known to someone else, and their sessions should end with it.
   - **The other way:** a "Keep my other devices signed in" box, unticked by default. More to test, and easy to get wrong in the moment.
3. **Should the device list show where a device signed in from (its address)?**
   - **Recommended:** no. The session row keeps none, and behind a tunnel or a proxy it would often be the proxy's. The browser and the times say enough to tell one's own devices apart.
   - **The other way:** keep the sign-in address with the session (a migration), and show it.

## For the test pass

- **Phone and laptop:** on the laptop, the device list shows both, with readable names. Sign the phone out from the laptop: the phone goes to the login page within seconds if the app is open, or on its next request otherwise.
- **Password:** change it on the laptop. The phone is signed out at once; the laptop stays signed in; the new password signs in on the phone; the old one does not.
- **Lockout:** type a wrong current password five times. The change is refused for a minute, and so is a login with that email on the phone.
- **Display name:** change it, and see it in the account menu at once, and as the author of the next renamed page's version.
