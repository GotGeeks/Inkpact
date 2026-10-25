// Finds and minimises a compaction input where applying [snapshot, ...updates] in ONE Yjs transaction
// loses a deletion that applying them one at a time keeps (run 0.5).
// Run with: pnpm compaction:repro   (writes spike/server/Spike.Server.Tests/Fixtures/yjs-one-transaction-repro.json)
import { mkdirSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import * as Y from 'yjs'
import { concurrentEdits } from './crdt-generator.ts'

const outDir = fileURLToPath(new URL('../../server/Spike.Server.Tests/Fixtures/', import.meta.url))
const { log } = concurrentEdits(50_500, 43, 'all')

const fingerprint = (doc: Y.Doc) => Buffer.from(Y.encodeSnapshot(Y.snapshot(doc))).toString('hex')
const apply = (inputs: Uint8Array[], oneTransaction: boolean) => {
  const doc = new Y.Doc()
  if (oneTransaction) Y.transact(doc, () => inputs.forEach((u) => Y.applyUpdate(doc, u)))
  else inputs.forEach((u) => Y.applyUpdate(doc, u))
  return doc
}
const diverges = (snapshot: Uint8Array, updates: Uint8Array[]) =>
  fingerprint(apply([snapshot, ...updates], true)) !== fingerprint(apply([snapshot, ...updates], false))

// Random compaction boundaries, as the stress test has them, until one diverges.
let found: { snapshot: Uint8Array; updates: Uint8Array[] } | null = null
for (let schedule = 1; schedule <= 100 && !found; schedule++) {
  let state = schedule * 7919
  const next = (lo: number, hi: number) => {
    state = (state * 1664525 + 1013904223) >>> 0
    return lo + (state % (hi - lo))
  }
  let snapshot: Uint8Array | null = null
  for (let from = 0; from < Math.min(log.length, 12_000); ) {
    const updates = log.slice(from, from + next(100, 500))
    if (snapshot && diverges(snapshot, updates)) {
      found = { snapshot, updates }
      break
    }
    snapshot = Y.encodeStateAsUpdate(apply(snapshot ? [snapshot, ...updates] : updates, false))
    from += updates.length
  }
}
if (!found) throw new Error('No diverging compaction found in 100 schedules')

// Delta debugging: drop chunks of updates while the divergence stays.
let { updates } = found
for (let granularity = 2; updates.length >= 2; ) {
  const chunk = Math.ceil(updates.length / granularity)
  let reduced = false
  for (let start = 0; start < updates.length; start += chunk) {
    const rest = [...updates.slice(0, start), ...updates.slice(start + chunk)]
    if (rest.length > 0 && diverges(found.snapshot, rest)) {
      updates = rest
      granularity = Math.max(granularity - 1, 2)
      reduced = true
      break
    }
  }
  if (!reduced) {
    if (granularity >= updates.length) break
    granularity = Math.min(granularity * 2, updates.length)
  }
}

const toBase64 = (bytes: Uint8Array) => Buffer.from(bytes).toString('base64')
mkdirSync(outDir, { recursive: true })
writeFileSync(
  `${outDir}yjs-one-transaction-repro.json`,
  JSON.stringify(
    {
      description:
        'A snapshot and updates that give different delete sets when applied in one Yjs transaction ' +
        '(a deletion is lost) than when applied one at a time (as a browser does). Found by compaction-stream seed 43.',
      snapshot: toBase64(found.snapshot),
      updates: updates.map(toBase64),
    },
    null,
    1,
  ),
)
console.log(`minimal: snapshot (${found.snapshot.length} bytes) + ${updates.length} updates (from ${found.updates.length})`)
