# Phase 0 spike findings

Written in run 0.7. Sources: `docs/runs/0.1.md` to `0.6.md`. The spike code was deleted after the gate and stays reachable at the git tag `phase0-spike` (for example `git show phase0-spike:spike/server/Spike.Server/Compactor.cs`). This file and the scenarios in the last section are what survive.

## Gate

> Concurrent and offline edits always merge with nothing lost, and storage does not grow without bound after compaction.

**Met.** The reconnect fix from 0.6 (line 21 of the Android checklist) was retested on the phone and works.

- Concurrent and offline edits: 5 Playwright scenarios, 20 of 20 in a repeated run, plus 5 headless scenarios in `check:sync`.
- Storage: after 50,000-update runs on 3 seeds the snapshot is exactly the size of a fresh encode, so it grows with the document and not with the edit history. Live updates stay bounded by the compaction cadence. The archive grows until the purge, which exists since run 1.10b (30 days, in batches).

## What worked

- **Relay of opaque blobs (0.1, 0.2).** The hub never decodes Yjs. One WebSocket per session carries several pages with a `[type][pageId][payload]` frame; frames never leak between pages.
- **Store, then broadcast, under one lock per page (0.2).** With an ordered send queue per connection, a subscriber gets every update exactly once, either in `SYNC` or live. 200 sequential edits cost 2.1 ms each round trip.
- **Presence (0.2).** Awareness is relayed and cached in memory, never stored. A dropped connection is removed with the same clock and a null state, so a re-announce at clock + 1 is accepted again.
- **Several tabs (0.2).** Each tab has its own Yjs client id and tabs only meet through the server: no duplicate rows.
- **Compaction (0.5).** All guarantees from the architecture hold and have tests: transactional, watermark, advisory lock, idempotent, verified, archived.
- **TipTap on Android (0.6).** Chrome and Firefox with Gboard and SwiftKey: typing, autocorrect, suggestions, glide and voice typing, selection, markdown shortcuts, lists, keyboard open and close, rotation, emoji and a long page all worked.

## Decision: Yjs sidecar, not YDotNet

**Use a small Node sidecar running Yjs for everything that applies updates on the server: compaction, its verification and text extraction. Do not use YDotNet in 1.10.** (Evaluated in 0.4, confirmed by 0.5.)

| Operation | yrs (YDotNet 0.6.0) | Yjs sidecar |
| --- | --- | --- |
| Updates in causal order (merge, state vector, diff, idempotence) | ✅ byte-identical to Yjs | ✅ |
| Updates out of causal order | ❌ | ✅ |
| Merged multi-client diff on top of existing state | ❌ | ✅ |
| Deletion that arrives before its content | ❌ deletion lost | ✅ |
| Half of an emoji deleted | ❌ the next character is lost | ✅ |

- The yrs failure is silent: a wrong snapshot that every browser would load, and a verification that also runs in yrs cannot catch it. A 2-update reproduction exists (`Fixtures/yrs-out-of-order-repro.json` in the spike); it was not reported upstream.
- Numbers on 9,926 updates (220 KB): merge 14 ms (yrs) against 33 ms (sidecar), text 5 against 8 ms, diff for a state vector 4 against 8 ms. Both give a 104 KB snapshot. The sidecar uses about 104 MB.
- Costs: Node in the image, a supervised child process (restart on crash, health check), binary framing instead of base64 JSON, and a Debian-based image. `YDotNet.Native.Linux` has no `linux-musl-arm64` build, so Alpine on arm64 would not load yrs anyway.
- Re-evaluate yrs when YDotNet updates; the `Known_divergence_*` tests pin today's behaviour.

**Consequences for 1A:**

1. The server can answer sync step 1 with a diff (8 ms for 10,000 updates), as the architecture describes. The 0.2 choice of letting the client compute the diff was a workaround for not decoding on the server; undo it in 1.10, together with the sidecar (moved there in run 1.09, which kept the hub free of decoding). Keep the client-side fallback only if the sidecar is down.
2. Compaction merges updates **one at a time**, never in one `Y.transact` (see the next section). Run 0.4 found the opposite for yrs; that does not matter once yrs is out.
3. Compaction should load a doc and encode its state. `Y.mergeUpdates` took 632 ms for 9,926 updates and gave 138 KB against 104 KB, because it cannot drop deleted content.
4. Verification compares Yjs snapshot fingerprints (state vector plus delete set), never text.

## Yjs finding: one transaction can lose a deletion

Applying a snapshot and the updates after it in a single `Y.transact` can end without a deletion that every browser has (1 deleted item out of 996 in the case found). The text is the same, so only a comparison of delete sets sees it. It happens in about 1 of 1,000 compactions. Applying the inputs one at a time matched a snapshot-free replay in all 1,012 chained compactions over 30 schedules. Minimal reproduction in the spike: a 14 KB snapshot plus 52 updates. Not diagnosed to a root cause and not reported upstream.

## Numbers from the stress test (0.5)

3 seeds of about 50,000 updates (50,500 edits by 3 concurrent clients on a TipTap document), appended while a compactor runs. About 40 s per seed. After the fix, 15 runs in 5 rounds, all green.

| Per seed | Value |
| --- | --- |
| Compactions | 55–62, one per 100–500 updates |
| Injected crashes, each leaving the state unchanged | 20–26 |
| Two compactions of one page at once | 7–8, always exactly one skipped |
| Final snapshot | 372–441 KB, equal to a fresh encode |
| Update log | 1.22 MB |
| Peak live updates between compactions | 60–90 KB |
| Archive | every folded update, exactly once |

- **The read race is real.** Reading the snapshot and the live updates in separate read-committed statements loses archived updates when a compaction commits in between; the subscribe-race test failed 5 of 5 times that way. Use one `REPEATABLE READ` transaction.
- Verification replays the full history on every compaction. Fine at 50,000 updates, unbounded later: sample it in 1.10.

## Android (0.6)

Tested on one phone: Chrome and Firefox, Gboard and SwiftKey (Firefox with Gboard was skipped on purpose). Not tested: HTTPS, service workers, iOS.

- **Reconnect (line 21): found, fixed, retested on the phone.** When a phone loses its network nobody closes the WebSocket, so the client kept writing into a dead connection and never reconnected. Clean disconnects in earlier tests hid this. Fix: client ping every 3 s and drop after 8 s of silence (one-byte ping and pong, any message counts as life), immediate drop on `offline` and immediate reconnect on `online`, late close events from replaced sockets ignored, server keep-alive ping every 15 s. Two e2e scenarios cover it. The server keep-alive is not covered by a test.
- **Undo (line 15): not solved.** SwiftKey offers no undo. Gboard's works one character at a time and seems to need two taps. The editor needs its own undo and redo buttons.
- Everything else worked on all three tested combinations.

## For the 1A runs

| Run | Take from the spike |
| --- | --- |
| 1.09 (sync hub) | Frame format and per-page lock with store-before-broadcast. Ping and pong with a rate limit, server keep-alive, bounded send queues, evict idle pages, auth, Origin and permission check on subscribe. (Answering sync step 1 with a diff moved to 1.10, with the sidecar.) |
| 1.10 (compaction) | Sidecar as the only decoder, supervised, with binary framing. One-at-a-time merge, fingerprint verification, repeatable-read load. Sample the full-history verification, idle-time trigger, 30-day archive purge, FsCheck property tests (new dependency, ask first), a multi-page worker design. Image based on Debian. |
| 1.11, 1.12 (editor) | Create the first paragraph once at page creation: two clients typing into an empty page each make their own, giving two paragraphs (nothing lost). Done in 1.11b: every client applies one fixed update; the browser scenarios now end with exactly one paragraph. Undo and redo buttons for mobile. Lazy-load the editor route (spike bundle 507 kB, 160 kB gzipped). Hidden tabs throttle timers, so a socket may drop and reconnect now and then; harmless. |
| Phase 1B | Persisting Yjs state in IndexedDB: a reload while offline still loses unsent edits today. |

Dev gotcha: a stale Vite process can load Yjs twice and stop sync. Restart Vite and delete `node_modules/.vite`.

## Sync scenarios to keep as integration tests in 1A

From the spike's Playwright suite (`spike/client/e2e/sync.spec.ts` at the tag), `check:sync` and the server tests:

1. **Concurrent typing.** Two browser contexts type into one page at once; both end with identical text containing every character, in order.
2. **Offline and back.** One browser's socket is cut and reconnects are refused; it shows disconnected, both sides keep typing, the online side does not see the offline edit; after going back online both converge.
3. **Server restart mid-edit.** Kill the server; both browsers keep typing and show disconnected; restart; both converge. A new third browser gets the same text from Postgres alone.
4. **Silent death.** The socket stays open but nothing passes (`blackhole()`); both converge about 10 s after the client notices.
5. **Offline and online events.** The browser's `offline` and `online` events drop and re-establish the connection; texts merge within seconds.
6. **Late join.** A client joining after edits receives the current text.
7. **Page isolation.** A client on another page sees nothing; one socket carrying several pages never leaks frames.
8. **Store once.** Two tabs of one user store each edit exactly once; reconnecting after offline edits stores exactly one diff.
9. **Subscribe during streaming.** A client subscribing while 300 updates stream in gets each exactly once.
10. **Subscribe during compaction.** A subscriber gets every update, in the snapshot or the list, never neither.
11. **Presence.** One user's tabs group ("jos (2 tabs)"); a closed tab or dropped connection disappears within the keep-alive window; a reconnecting client's re-announce is accepted.
12. **Compaction guarantees.** Crash at each stage leaves state unchanged; updates appended during compaction stay live; two concurrent compactions give one skip; compacting twice is a no-op; a corrupted merge aborts; a non-TipTap page compacts.
13. **Compaction stress.** 50,000 edits by 3 clients with compaction every few hundred updates, injected crashes and parallel compactions; page and snapshot equal an uncompacted Yjs oracle by fingerprint, rows balance (live plus archive equals appended), snapshot equals a fresh encode.
14. **Codec contract.** Merge, state vector, diff, idempotence, snapshot plus overlapping updates, incremental compaction, out-of-order updates, deletion before content, split emoji; fixtures from the real TipTap schema.
