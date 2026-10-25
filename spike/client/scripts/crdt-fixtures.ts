// Generates Yjs fixtures for the YDotNet evaluation (run 0.4), written by real Yjs with TipTap's schema.
// Run with: pnpm crdt:fixtures   (writes to spike/server/Spike.Crdt.Tests/Fixtures)
import { writeFileSync, mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import * as Y from 'yjs'
import { prosemirrorJSONToYXmlFragment } from '@tiptap/y-tiptap'
import { plainText } from './crdt-fixtures-text.ts'
import { FIELD, concurrentEdits, paragraph, schema, text, type EditMix } from './crdt-generator.ts'

const outDir = fileURLToPath(new URL('../../server/Spike.Crdt.Tests/Fixtures/', import.meta.url))

const toBase64 = (bytes: Uint8Array) => Buffer.from(bytes).toString('base64')

function stateVectorMap(doc: Y.Doc) {
  return Object.fromEntries([...Y.decodeStateVector(Y.encodeStateVector(doc))].map(([k, v]) => [String(k), v]))
}

function writeFixture(name: string, edits: number, seed: number, mix: EditMix = 'all') {
  const { log, final } = concurrentEdits(edits, seed, mix)

  // The server-diff scenario: a client that has the first `partialCount` updates.
  const partialCount = Math.floor(log.length / 2)
  const partial = new Y.Doc()
  for (const update of log.slice(0, partialCount)) Y.applyUpdate(partial, update)

  const start = performance.now()
  const merged = Y.mergeUpdates(log)
  const mergeMs = performance.now() - start

  const fixture = {
    description: `${edits} random edits (${mix}) by 3 clients on a TipTap document, seed ${seed}`,
    updates: log.map(toBase64),
    expectedText: plainText(final),
    expectedXml: final.getXmlFragment(FIELD).toString(),
    expectedStateVector: stateVectorMap(final),
    partialCount,
    partialStateVector: toBase64(Y.encodeStateVector(partial)),
    jsMergeBytes: merged.length,
    // A loaded doc garbage-collects deleted content, so its full-state encoding is smaller than a merge.
    jsStateBytes: Y.encodeStateAsUpdate(final).length,
    jsMergeMs: Math.round(mergeMs * 10) / 10,
  }
  mkdirSync(outDir, { recursive: true })
  writeFileSync(`${outDir}${name}.json`, JSON.stringify(fixture, null, 1))
  const bytes = log.reduce((sum, update) => sum + update.length, 0)
  console.log(
    `${name}: ${log.length} updates, ${bytes} bytes in the log, merged ${merged.length} bytes (Y.mergeUpdates ${fixture.jsMergeMs} ms)`,
  )
}

writeFixture('tiptap-small', 300, 1)
writeFixture('tiptap-10k', 10_000, 2)
writeFixture('no-format-2k', 2_000, 3, 'no-format')
writeFixture('insert-only-2k', 2_000, 4, 'insert-only')

// Edge case: a Yjs client deletes half of an emoji's surrogate pair. Yjs (UTF-16) allows it;
// yrs stores UTF-8 and cannot hold a lone surrogate. Documents what the server does with it.
{
  const doc = new Y.Doc()
  doc.clientID = 998
  prosemirrorJSONToYXmlFragment(schema, { type: 'doc', content: [paragraph(text('a😀b'))] }, doc.getXmlFragment(FIELD))
  const log = [Y.encodeStateAsUpdate(doc)]
  doc.on('update', (update: Uint8Array) => log.push(update))
  const node = (doc.getXmlFragment(FIELD).get(0) as Y.XmlElement).get(0) as Y.XmlText
  node.delete(2, 1) // the low surrogate of 😀
  writeFileSync(
    `${outDir}surrogate-split.json`,
    JSON.stringify({ updates: log.map(toBase64), expectedText: plainText(doc) }, null, 1),
  )
  console.log(`surrogate-split: text in Yjs is ${JSON.stringify(plainText(doc))}`)
}
