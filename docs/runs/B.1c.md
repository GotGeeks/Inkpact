# Run B.1c: The upload is gone when the import says it has ended

**Model:** S·low in the roadmap. Planned on Opus 5.5; builds on Sonnet 5.5 low. A two-line change in the server and one new test, in code that already has thorough tests. `/code-review` at low before the PR merges.

## Goal

When an import reports that it has ended ("completed" or "failed"), its uploaded file is already gone. Today the file is removed just after the end is written, so for a moment the import says "completed" while the file is still on disk. `ImportEndpointTests.An_uploaded_export_is_accepted_imported_in_the_background_and_reported` sees that moment now and then under a full parallel run (B.1b's full run). The test is right; the server's order is wrong.

## Inputs

- `docs/architecture.md`: the B.1c row in phase 1B.
- `docs/self-hosting.md`, "Where the file waits": "deleted when the import ends". That stays true, and becomes true at the moment the import says so.
- `server/src/Inkpact.Api/Import/ImportRunner.cs`: `RunAsync` (line 152), `FinishAsync` (line 1804), `DeleteFile`, `AbandonAsync`.
- `server/src/Inkpact.Api/Import/ImportWorker.cs`: `ExecuteAsync` calls `AbandonAsync` when `RunAsync` throws.
- Tests: `ImportEndpointTests` (`WaitForEndAsync`, `UploadedFiles`, the flaky test at line 164, and `A_second_export_waits_until_the_first_has_ended`, which makes the same assertion after an end), `ImportRunnerTests` (`StartAsync`, `ImportAsync`, `An_import_given_up_on_is_marked_failed_and_its_file_removed`).

## What is there now (found while planning)

1. **The order in `RunAsync`:** `ImportAsync`, then `FinishAsync` writes the state (`completed` or `failed`) with the counts and the report, and only then does a `finally` call `DeleteFile(run.Path)`. A `GET /api/imports/{id}` between the commit of that write and the delete sees the end with the file still there. The test polls every 100 ms, and under a full parallel run the worker can be held up in that window long enough for a poll to land in it.
2. **The file is of no use once `ImportAsync` returns.** Every read of it (`JexArchive.ReadIndexAsync`, `JexArchive.OpenSlice`) is under `await using`, so nothing has it open by then.
3. **The paths that do not reach the end are fine as they are:**
   - Cancelled (the app stops): `RunAsync` rethrows before `FinishAsync`, and the file stays. That is intended: the next start marks the import interrupted and removes stray files older than 5 minutes (`ImportWorker.RecoverAsync`).
   - `FinishAsync` gives up (the database is away): the worker calls `AbandonAsync`, which deletes the file and marks the import failed. `File.Delete` of a file that is already gone does nothing.
4. **Nothing reads the file after the end,** in the server or the client. The client shows the end from the row.

## Decisions

1. **Delete, then write the end.** In `RunAsync`, after the `try`/`catch` around `ImportAsync`: `DeleteFile(run.Path)`, then `await FinishAsync(run, error)`. The `try`/`finally` around `FinishAsync` goes away. The comment says why the order matters: the end is what the person and the tests read as "done", so the file must be gone before it.
2. **Cancellation stays as it is** (What is there now, point 3).
3. **A test that fails every time on the old order** (question 1), in `ImportRunnerTests`, with no change to the app for the test:
   - The test adds a trigger to its own database: `before update on imports`, which, when the state changes to `completed` or `failed`, takes a transaction advisory lock with a fixed key (`pg_advisory_xact_lock`). Advisory locks are per database, so other test classes are not affected.
   - The test takes that lock first, on a connection of its own (`pg_advisory_lock`), then starts the job without awaiting it.
   - It waits until `pg_locks` shows a waiter on that key (at most 30 seconds). At that moment the job is inside the write of its end, and the end is not committed yet.
   - It asserts that the file is gone, releases the lock, awaits the job, and asserts that the row has ended.
   - It runs for a good export (`completed`) and for a file that cannot be read (`failed`), as a `[Theory]`.
   - The `ImportAsync` helper is split so the test can make the row and the file and start the job itself; the existing tests keep using the helper unchanged.
4. **The existing endpoint tests stay as they are.** They asserted the right thing.

## Done when

- The new test passes, and fails on both of its cases when the old order is put back (a mutation check, recorded in the handoff).
- `ImportEndpointTests` and `ImportRunnerTests` pass ten times in a row (`dotnet test server --no-build --filter ...` in a loop) while a second loop runs the rest of the server tests next to them for load. This is not proof, since the window was rare, but it shows that nothing else in those classes is flaky. The new test is the proof.
- `pnpm check:push` is green, and `pnpm check` with Postgres up before the last push.

## Tests

- **`ImportRunnerTests`**, new: `The_file_is_gone_before_the_import_says_it_has_ended(bool readable)` as described in decision 3.
- **`ImportRunnerTests`**, unchanged and still passing: `An_import_given_up_on_is_marked_failed_and_its_file_removed` (the `AbandonAsync` path) and the tests of a stopped job.
- **`ImportEndpointTests`**: unchanged.
- While working: `dotnet test server --filter "FullyQualifiedName~Inkpact.Api.Tests.Import"`. Before the push, `pnpm check:push`. Before the last push, `pnpm check`. No client change, so no Playwright spec of its own.

## Docs to update

- None in `docs/architecture.md` or `docs/self-hosting.md`: "deleted when the import ends" was the intent all along.
- This card: answers and handoff.

## Out of scope

- Removing a file that a cancelled import leaves before the next start.
- Any change to the polling in `WaitForEndAsync` or other tests. Waiting in the test for the file to go would hide the server's order instead of fixing it.

## Questions

1. **How the new test catches the moment between the delete and the end:**
   - A trigger and an advisory lock in the test's own database (decision 3): deterministic, no code in the app for the test, and it uses the real Postgres the tests already have. **Recommended.**
   - A hooks interface in the runner (`IImportHooks.BeforeFinishAsync`, no-op in the app, replaced by the factory like `ICompactionHooks`): simpler to read, but it adds an interface to the app that only the tests use.
   - No new test, only the existing endpoint test: it would keep catching the old order now and then, not every time.

## For the test pass

Nothing: no behaviour a person can see changes.

## Answers

1. Went with the recommended option: a trigger on the test's own database (`before update on imports`) that takes `pg_advisory_xact_lock` when the state moves to `completed` or `failed`, and a second, unpooled connection in the test that holds `pg_advisory_lock` on the same key first. The test waits until `pg_locks` shows a waiter in its own database on that key, checks the file is gone, then releases the lock and awaits the job.

## Handoff

- `RunAsync` now deletes the file, then awaits `FinishAsync`; the `try`/`finally` that used to wrap `FinishAsync` is gone. Cancellation is unaffected: a cancelled run still rethrows before either call.
- A delete can no longer stop the end from being written. `ImportFiles.TryDelete(path, logger)` catches every exception and logs it; the runner, `AbandonAsync`, `ImportWorker.RecoverAsync` and the upload endpoint all use it (the endpoint's `DeleteQuietly` is gone, and the upload handler now takes an `ILoggerFactory`). Before it writes the end, the runner tries the delete up to 5 times, 200 ms apart, for a file a virus scanner or indexer holds for a moment. A file that still stays is logged, the end is written anyway, and the next start removes it. `RecoverAsync` also skips a file whose time cannot be read, so the loop goes on to the next file.
- New test `ImportRunnerTests.The_file_is_gone_before_the_import_says_it_has_ended(bool readable)`, a `[Theory]` for a good export (`completed`) and an unreadable file (`failed`). It only counts a waiter in its own database on its own key, since the Postgres server is shared with the other classes. It releases the lock and observes the job in a `finally`, so a failed assertion neither leaves the job blocked nor is hidden by the job's exception. If the job ends without reaching the trigger, the test surfaces the job's exception at once instead of timing out after 30 s.
- Mutation check, run again after the review fixes: with the old order (end first, then delete), both cases fail on `File.Exists(...).ShouldBeFalse()`, in about 3 s. With the fix, both pass.
- The `ImportAsync` test helper is split: `QueueAsync(byte[])` writes the file and the queued row; `ImportAsync(JexBuilder)` calls it and then runs the job. `A_file_that_is_not_an_export_ends_as_failed_with_a_code` now uses `QueueAsync` too. The same setup in `ReimportHarness` and `ReimportContentTests` is left alone (other classes, out of scope).
- `ImportRunnerTests` and `ImportEndpointTests` passed ten times in a row (584 tests each run) while the rest of the server suite (1,347 tests) ran alongside for load. That was before the review fixes; the import tests pass again after them.
- `/code-review low` ran twice and flagged that a failed delete could now skip the end. The full `/code-review` that followed made 7 findings; all are fixed as described above.
- `pnpm check` with Postgres up, after the review fixes: green. 1,309 web and 152 sidecar unit tests, 1,934 server tests (incl. the Slow timing tests), 381 Playwright specs passed, 0 failed.
