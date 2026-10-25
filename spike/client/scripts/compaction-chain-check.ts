// Evidence for run 0.5: a chain of compactions ([previous snapshot, ...new updates]) built two ways,
// compared with the snapshot-free truth (every update applied on its own, as a browser does).
// Run with: pnpm compaction:chain-check [seed]
import * as Y from 'yjs'
import { concurrentEdits } from './crdt-generator.ts'

const seed = Number(process.argv[2] ?? 43)
const { log } = concurrentEdits(50_500, seed, 'all')
const fp = (doc: Y.Doc) => Buffer.from(Y.encodeSnapshot(Y.snapshot(doc))).toString('hex')
const load = (inputs: Uint8Array[], one: boolean) => {
  const doc = new Y.Doc()
  if (one) Y.transact(doc, () => inputs.forEach((u) => Y.applyUpdate(doc, u)))
  else inputs.forEach((u) => Y.applyUpdate(doc, u))
  return doc
}

let schedules = 0, compactions = 0, oneTxWrong = 0, perUpdateWrong = 0
for (let schedule = 1; schedule <= 30; schedule++) {
  let state = schedule * 7919
  const next = (lo: number, hi: number) => { state = (state * 1664525 + 1013904223) >>> 0; return lo + (state % (hi - lo)) }
  const truth = new Y.Doc()
  let snapOne: Uint8Array | null = null
  let snapPer: Uint8Array | null = null
  schedules++
  for (let from = 0; from < Math.min(log.length, 10_000); ) {
    const updates = log.slice(from, from + next(100, 500))
    updates.forEach((u) => Y.applyUpdate(truth, u)) // the browsers' view: every update on its own
    const one = load(snapOne ? [snapOne, ...updates] : updates, true)
    const per = load(snapPer ? [snapPer, ...updates] : updates, false)
    compactions++
    if (fp(one) !== fp(truth)) oneTxWrong++
    if (fp(per) !== fp(truth)) perUpdateWrong++
    snapOne = Y.encodeStateAsUpdate(one)
    snapPer = Y.encodeStateAsUpdate(per)
    from += updates.length
  }
}
console.log(`${schedules} schedules, ${compactions} chained compactions`)
console.log(`one-transaction chain differs from the truth in ${oneTxWrong} compactions; per-update chain differs in ${perUpdateWrong}`)
