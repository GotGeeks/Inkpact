# Run B.7: Health, logs, backup and restore, and a password reset from the command line

**Model:** S·low in the roadmap; planned as S·med, because the run adds a command that writes a password hash and ends sessions, a job that deletes rows, a migration and an anonymous endpoint. Planned on Opus 5.5 high; builds on Sonnet 5.5 medium. `/code-review` at medium before the PR merges: the password reset is an authentication path. One PR, branch `feat/b7-operations`.

**Status:** Planned.

## Goal

The person who runs Inkpact can see whether it works, read its log, get their notes back after a lost disk, and get into the app again after forgetting the only administrator password. Each of these works the same in the standard compose stack, on Unraid and with a PostgreSQL server they already run, and none of it needs the app's interface.

## Inputs

- [`docs/architecture.md`](../architecture.md): the B.7 row; "Database migrations" (the dump before a migration); "Security from day one" (sessions, passwords, login throttling, the audit log and its missing retention, the trusted administrator); "Deferred on purpose" (backup targets: lose at most a day, restore within an hour; password reset by email before the first public release, CLI until then; the metrics endpoint).
- [`docs/self-hosting.md`](../self-hosting.md): "What to back up", "Updating", "With a PostgreSQL you already run", "Unraid".
- [`docs/runs/1.04.md`](1.04.md), [`1.05.md`](1.05.md), [`1.06.md`](1.06.md) and [`1.13.md`](1.13.md): the dump before a migration, the account and sessions, login throttling and the audit log, the image and the compose file.
- [`docs/test-pass.md`](../test-pass.md): the hands-on checks below are deferred to run 2.10b.
- Code:
  - [`Program.cs`](../../server/src/Inkpact.Api/Program.cs) (the health endpoint, the pipeline), [`appsettings.json`](../../server/src/Inkpact.Api/appsettings.json) (logging);
  - [`PasswordHashing.cs`](../../server/src/Inkpact.Api/Auth/PasswordHashing.cs), [`AuthEndpoints.cs`](../../server/src/Inkpact.Api/Auth/AuthEndpoints.cs) (the password rules, logout everywhere), [`PostgresTicketStore.cs`](../../server/src/Inkpact.Api/Auth/PostgresTicketStore.cs), [`LoginThrottling.cs`](../../server/src/Inkpact.Api/Auth/LoginThrottling.cs), [`AuditLog.cs`](../../server/src/Inkpact.Api/Auth/AuditLog.cs) and [`AuditEvent.cs`](../../server/src/Inkpact.Data/Entities/AuditEvent.cs);
  - [`ArchivePurge.cs`](../../server/src/Inkpact.Api/Compaction/ArchivePurge.cs): the pattern for a bounded, restartable purge;
  - [`IYjsCodec.cs`](../../server/src/Inkpact.Api/Crdt/IYjsCodec.cs): the sidecar's calls;
  - [`Dockerfile`](../../Dockerfile) (the `HEALTHCHECK`, the `ENTRYPOINT`), [`deploy/docker-compose.yml`](../../deploy/docker-compose.yml), [`deploy/inkpact.sh`](../../deploy/inkpact.sh), [`.github/workflows/image.yml`](../../.github/workflows/image.yml);
  - [`EndpointPolicyTests.cs`](../../server/tests/Inkpact.Api.Tests/Permissions/EndpointPolicyTests.cs) (the reviewed anonymous endpoints) and [`CapturingLoggerProvider.cs`](../../server/tests/Inkpact.Api.Tests/Testing/CapturingLoggerProvider.cs).

## What is there now (found while planning)

1. **`GET /api/health` says `ok` whenever the process answers.** It reads nothing: with the database down, the container is still "healthy", and the sidecar's state is not reported anywhere. The `Dockerfile`'s `HEALTHCHECK`, `deploy/inkpact.sh status` and the Playwright web server all call it.
2. **The log is ASP.NET's default console output:** two lines per entry, in colour codes when the console is not a terminal, local times. Messages already use templates with named values (`{WorkspaceId}`, `{Count}`), so a structured format costs nothing in the code. There is no rule written down about what a log line may contain; the setup code is written to the log on purpose.
3. **Backups are a paragraph.** "What to back up" names the two volumes and says to use `pg_dump`; there are no commands, no schedule, no restore procedure, and nothing has ever restored one. The dumps written before a migration (`/data/backups`) are never removed.
4. **A forgotten password has no way back.** Setup is closed once an account exists, there is no email, and the only administrator who forgets their password can only edit the database by hand.
5. **The audit log has no retention:** the architecture leaves it to this run. `audit_events` has no index on `occurred_at`.
6. **Logout everywhere** removes the user's sessions and tells the sync hub, which closes their sockets at once (`SessionEvents.AllSessionsEnded`). Another process cannot tell the hub; its periodic session check (every five minutes, `Sync:SessionCheckInterval`) closes them then.

## Decisions

1. **Health: one endpoint for "running", one for "ready".**
   - `GET /api/health` stays as it is: anonymous, no database, `{"status":"ok"}`. It is the liveness check and keeps its callers.
   - `GET /api/health/ready`, anonymous: the **database** (a trivial query, and no pending migration) and the **sidecar** (one small call, within two seconds). The answer is `{"status": "ok" | "degraded" | "unhealthy", "checks": {"database": ..., "sidecar": ...}}`: `200` for ok and degraded, `503` for unhealthy. A sidecar that does not answer is *degraded* (editing and sync go on; compaction, search text and imports wait), a database that does not answer is *unhealthy*. The answer holds no exception text, version, host name or timing. Built on ASP.NET Core's health checks (part of the shared framework: no new package), with the result kept for five seconds so the anonymous endpoint cannot be used to load the database. Added to the reviewed anonymous endpoints with its reason.
   - The image's `HEALTHCHECK` calls `/api/health/ready`, with a start period long enough for the dump before a migration on a large library (five minutes); Docker does not restart an unhealthy container, so the state is for the person reading Unraid's Docker tab or `docker ps`. `deploy/inkpact.sh status` reports both.
2. **Logs: one line per entry, JSON on request.**
   - The default is the simple console format on one line, with a UTC timestamp and no colour codes, readable in `docker logs` and in Unraid's log viewer. `Logging__Console__FormatterName=json` switches to ASP.NET's JSON console format, with scopes, for a log collector (Loki, Graylog). Configuration only; no new package.
   - **What a log line may hold:** ids, counts, codes, durations and the request's trace id. Never note content, page or tag titles, search text, file names, typed email addresses, passwords, tokens or cookies. The setup code stays the one deliberate exception, while there is no account. The rule is written in `CLAUDE.md`'s backend conventions and tested (below).
3. **Backup and restore: documented commands, a script for the external-database setup, and a test that follows the documentation.**
   - **Backing up** is two things together: the database (`pg_dump --format=custom`, safe while the app runs) and `/data` without `imports` (attachments, keys, the dumps before migrations). For the compose stack the guide gives the two commands and a nightly schedule (a cron line, and Unraid's User Scripts plugin), with an example that keeps the last 14. `deploy/inkpact.sh backup [folder]` does the same for the external-database setup.
   - **Restoring** needs the same or a newer Inkpact than the one that made the dump: stop the app, restore `/data` (owned by UID 1654), restore the dump into an empty database (`pg_restore --no-owner --role inkpact`), start the app (a newer version migrates it, writing its own dump first). `deploy/inkpact.sh restore <folder>` does it for the external-database setup and refuses a database that is not empty. The guide says what a restore loses (anything after the dump, sessions only if the keys were not restored) and how to check it worked.
   - The targets stay the architecture's: lose at most a day (a nightly backup), restore within an hour. The run does not add a scheduler to the app: the admin page with backups is run 2.9.
   - The dumps before migrations are still never removed by the app; the guide says how to remove old ones. Pruning them is not in this run.
4. **Password reset from the command line.**
   - `dotnet Inkpact.Api.dll reset-password <email>`, run in the container (`docker exec -it inkpact dotnet Inkpact.Api.dll reset-password you@example.com`, Unraid's container console, or `deploy/inkpact.sh reset-password <email>`). It starts no web server, no workers and no migration: it reads the same configuration, refuses to run against a database with pending migrations (start the app once first) or a newer one, and exits with a non-zero code and a one-line message on every refusal.
   - The new password is asked twice on the terminal without echo, or read from standard input with `--password-stdin` (for scripts); never from an argument, which `ps` and the shell history would keep. `--generate` makes a random password of 24 characters and prints it once. The password rules are the app's (12 to 128 characters), through the same validator; the hash is the same Argon2id.
   - In one transaction: the new hash, every session of the user removed (as logout everywhere), the login lockout for that email cleared, and an audit event `password_reset` with no address (it came from the console). Open sockets close at the hub's next session check (within `Sync:SessionCheckInterval`); the guide says so.
   - Any account can be reset, administrator or not: whoever can run a command in the container is the trusted administrator. An unknown email is reported as such: the console is not the login form.
5. **Audit retention.** `Audit:RetentionDays`, 365 by default; `0` keeps everything. A daily job deletes older events in bounded batches (as the archive purge), with a migration that adds an index on `occurred_at`. Every event type follows the same rule. The architecture's "no retention yet (run B.7)" becomes the rule.
6. **No new dependency** (npm or NuGet), no change to the web client, and no setting that is required: an existing install upgrades without touching its compose file.

## Build order

1. The two health endpoints, the checks, the cached result, the `HEALTHCHECK` and the policy test.
2. The logging configuration, the rule in `CLAUDE.md` and the log content test.
3. The audit retention setting, the migration and the job.
4. The `reset-password` command and the `deploy/inkpact.sh` wrapper.
5. `deploy/inkpact.sh backup` and `restore`, the backup and restore section of the guide, and the restore test in `image.yml`.
6. The guide's sections on health, logs and the password reset; this card's handoff; the test-pass checks.

## Done when

- `/api/health/ready` says unhealthy (503) with the database down and degraded with the sidecar down, and says nothing else about the server; the container shows unhealthy in `docker ps` without the database and healthy again when it is back, and a long pre-migration dump does not mark it unhealthy.
- The log is one line per entry by default and JSON with one setting, and no test flow writes note content, titles, search text, file names, typed addresses or secrets into it.
- Following the guide's commands, a backup of a stack with pages, tags and attachments restores into an empty stack with everything there and sign-in working.
- `reset-password` changes the password, signs the user out everywhere and clears a lockout; it refuses pending migrations, unknown emails and passwords outside the rules, and never takes the password from an argument.
- Audit events older than the retention are deleted in bounded batches, and `0` keeps them.

## Tests

- **Server, real Postgres:** both health endpoints anonymous and in the reviewed list; ready with the database up, unreachable (a wrong connection string) and with a pending migration; the sidecar stopped or slow (degraded); the cache (two calls within five seconds reach the database once); the answer's fields only.
- **Logs:** with `CapturingLoggerProvider`, run a failed login with a typed address, a search, a page create and rename, a tag, an attachment upload and an import, each with marker strings, and assert no marker appears in any log entry. The JSON format writes one parseable object per entry (an options test, not a snapshot of ASP.NET's format).
- **Password reset:** the command against a Testcontainers database: the hash changes and verifies, the old one fails, every session of that user is gone and another user's are not, the lockout row is gone, the audit event is written, all in one transaction (a failure part-way changes nothing). Refusals: unknown email, pending migrations, a newer database, too short and too long, mismatched confirmation, an argument given as the password. `--password-stdin` and `--generate`. The command starts no hosted service (no worker runs, no migration, no dump).
- **Audit retention:** `MutableTimeProvider` just before, at and after the boundary; batches; `0`; a crash mid-batch and a retry; other tables untouched.
- **Backup and restore in CI:** a script in `image.yml`, after the image is built: start the compose stack with it, create the administrator, a page, a tag and an attachment through the API, back up with the guide's commands, `docker compose down -v`, restore, start, and check the page, the tag, the attachment's bytes and a sign-in. `deploy/inkpact.sh backup` and `restore` are checked by `shellcheck` and run in the same job against the stack's Postgres if it fits; otherwise by the test pass.
- **While building:** the server test classes you change, `dotnet format`, `shellcheck` on the scripts. Before each push `pnpm check:push`; before the last push `pnpm check` once with Postgres up.

## Docs to update

- [`docs/self-hosting.md`](../self-hosting.md): "What to back up" becomes "Backup and restore" (commands, schedule, restore, what it loses, checking it); health and logs; the password reset; removing old pre-migration dumps.
- [`docs/architecture.md`](../architecture.md): the audit log's retention, in the same PR.
- [`CLAUDE.md`](../../CLAUDE.md): the rule about what a log line may hold.
- [`docs/test-pass.md`](../test-pass.md): the checks below, under "B.7".
- This card: the handoff when built.

## Out of scope

- The metrics endpoint and request timings (run 2.10), password reset by email (2.10), the admin page with users and backups (2.9).
- A scheduler or backup target inside the app, S3, and pruning the dumps before migrations.
- Log shipping, request logs, and log retention (Docker's log driver decides).
- Trash retention, which the architecture lists as deferred.
- The release pipeline, the Unraid template and the update check (B.8).
- The unfinished parts of B.5 (version history): B.7 does not depend on them.

## Questions

1. **How long is the audit log kept?** Recommended: 365 days, configurable, `0` for always.
2. **Which log format is the default?** Recommended: one readable line per entry, JSON with one setting. The other way round suits a log collector but makes `docker logs` and Unraid's viewer hard to read.
3. **Does the app back itself up?** Recommended: not in this run. The commands and the scripts are documented and tested; a nightly job inside the app, with somewhere to put the dumps, belongs with the admin page (2.9).
4. **Should the container show unhealthy when the database is down?** Recommended: yes (the `HEALTHCHECK` on `/ready`). Docker does not restart it, so it only changes what `docker ps` and Unraid show.

## For the test pass

- On Unraid, schedule the nightly backup with User Scripts, let it run, then restore it into a spare stack within an hour: notes, tags, attachments and sign-in are there.
- Stop Postgres while the app runs: Unraid's Docker tab shows Inkpact unhealthy, and healthy again after Postgres is back. Upgrade across a migration with the real library: the dump before it does not mark the container unhealthy.
- Reset the password from the container console, sign in with it, and check that the other signed-in browser (the phone) is signed out within five minutes.
- Read the app's log in Unraid's log viewer: one line per entry, nothing from a note in it.
