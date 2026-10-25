// Writes the update stream for the compaction stress test (run 0.5): N random edits by 3 concurrent
// clients on a TipTap document, as the server would receive them. Deterministic per seed.
// Usage: node scripts/compaction-stream.ts <edits> <seed> <outFile>
// File format: repeated [uint32 little-endian length][Yjs v1 update bytes].
import { writeFileSync } from 'node:fs'
import { concurrentEdits } from './crdt-generator.ts'

const [edits, seed, outFile] = process.argv.slice(2)
if (!edits || !seed || !outFile) throw new Error('Usage: compaction-stream.ts <edits> <seed> <outFile>')

const { log } = concurrentEdits(Number(edits), Number(seed), 'all')

const parts: Buffer[] = []
for (const update of log) {
  const length = Buffer.alloc(4)
  length.writeUInt32LE(update.length)
  parts.push(length, Buffer.from(update))
}
writeFileSync(outFile, Buffer.concat(parts))
console.log(`${log.length} updates, ${parts.reduce((sum, part) => sum + part.length, 0)} bytes -> ${outFile}`)
