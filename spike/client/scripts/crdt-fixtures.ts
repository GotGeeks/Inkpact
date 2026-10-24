// Generates Yjs fixtures for the YDotNet evaluation (run 0.4), written by real Yjs with TipTap's schema.
// Run with: pnpm crdt:fixtures   (writes to spike/server/Spike.Crdt.Tests/Fixtures)
import { writeFileSync, mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import * as Y from 'yjs'
import { getSchema } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { prosemirrorJSONToYDoc } from '@tiptap/y-tiptap'
import { plainText } from './crdt-fixtures-text.ts'

const outDir = fileURLToPath(new URL('../../server/Spike.Crdt.Tests/Fixtures/', import.meta.url))
const schema = getSchema([StarterKit])
const FIELD = 'default' // the fragment name TipTap's Collaboration extension uses

const text = (value: string, marks?: string[]) => ({
  type: 'text',
  text: value,
  ...(marks ? { marks: marks.map((type) => ({ type })) } : {}),
})
const paragraph = (...content: object[]) => ({ type: 'paragraph', content })

const richDocument = {
  type: 'doc',
  content: [
    { type: 'heading', attrs: { level: 1 }, content: [text('Boodschappen & plannen')] },
    paragraph(text('Een '), text('vetgedrukte', ['bold']), text(' en '), text('schuine', ['italic']), text(' zin met é, ü en 🚀.')),
    {
      type: 'bulletList',
      content: [
        { type: 'listItem', content: [paragraph(text('melk'))] },
        {
          type: 'listItem',
          content: [
            paragraph(text('brood')),
            { type: 'orderedList', attrs: { start: 1 }, content: [{ type: 'listItem', content: [paragraph(text('volkoren'))] }] },
          ],
        },
      ],
    },
    { type: 'codeBlock', attrs: { language: 'ts' }, content: [text('const x = 1\nconsole.log(x)')] },
    { type: 'blockquote', content: [paragraph(text('Citaat met '), text('code', ['code']))] },
    { type: 'horizontalRule' },
    paragraph(text('Laatste alinea.')),
  ],
}

/** Deterministic pseudo-random numbers, so the fixtures are reproducible. */
function random(seed: number) {
  return () => {
    seed = (seed * 1664525 + 1013904223) >>> 0
    return seed / 2 ** 32
  }
}

/** All XmlText nodes inside paragraphs and headings, where TipTap allows marked text. */
function editableTexts(fragment: Y.XmlFragment): Y.XmlText[] {
  const found: Y.XmlText[] = []
  const walk = (node: Y.XmlElement | Y.XmlFragment) => {
    for (const child of node.toArray()) {
      if (child instanceof Y.XmlText) {
        if (node instanceof Y.XmlElement && ['paragraph', 'heading'].includes(node.nodeName)) found.push(child)
      } else if (child instanceof Y.XmlElement) walk(child)
    }
  }
  walk(fragment)
  return found
}

/** The raw characters of an XmlText, without the mark tags toString() adds. */
const rawText = (node: Y.XmlText) =>
  (node.toDelta() as { insert: unknown }[]).map((op) => (typeof op.insert === 'string' ? op.insert : '')).join('')

/**
 * Moves a UTF-16 index off the middle of a surrogate pair. Editors never split an emoji; Yjs would
 * let a random index do it, and that case gets its own fixture.
 */
function boundary(value: string, index: number) {
  const code = value.charCodeAt(index)
  return index > 0 && index < value.length && code >= 0xdc00 && code <= 0xdfff ? index - 1 : index
}

const WORDS = ['kaas', 'appel', 'thee', 'notitie', 'Ünïcode', '日本', '😀', ' ', ' ', ', ']
const MARKS = ['bold', 'italic', 'strike', 'code']

/**
 * Three clients edit concurrently and sync at random moments. Every update is recorded in the order
 * the server would receive it.
 */
type EditMix = 'all' | 'no-format' | 'insert-only'

function concurrentEdits(edits: number, seed: number, mix: EditMix) {
  const rnd = random(seed)
  const base = prosemirrorJSONToYDoc(schema, richDocument, FIELD)
  const baseUpdate = Y.encodeStateAsUpdate(base)

  const log: Uint8Array[] = [baseUpdate]
  const clients = [0, 1, 2].map((i) => {
    const doc = new Y.Doc()
    doc.clientID = 1000 + i
    Y.applyUpdate(doc, baseUpdate, 'remote')
    const outbox: Uint8Array[] = []
    doc.on('update', (update: Uint8Array, origin: unknown) => {
      if (origin !== 'remote') outbox.push(update)
    })
    return { doc, outbox }
  })

  const flush = (sender: (typeof clients)[number]) => {
    for (const update of sender.outbox.splice(0)) {
      log.push(update)
      for (const other of clients) if (other !== sender) Y.applyUpdate(other.doc, update, 'remote')
    }
  }

  for (let i = 0; i < edits; i++) {
    const client = clients[Math.floor(rnd() * clients.length)]
    const fragment = client.doc.getXmlFragment(FIELD)
    const texts = editableTexts(fragment)
    const roll = rnd()

    if (mix === 'insert-only' && texts.length > 0) {
      const target = texts[Math.floor(rnd() * texts.length)]
      const raw = rawText(target)
      target.insert(boundary(raw, Math.floor(rnd() * (raw.length + 1))), WORDS[Math.floor(rnd() * WORDS.length)])
    } else if (roll < 0.03) {
      // New paragraph at a random top-level position.
      const index = Math.floor(rnd() * (fragment.length + 1))
      client.doc.transact(() => {
        const element = new Y.XmlElement('paragraph')
        element.insert(0, [new Y.XmlText(WORDS[Math.floor(rnd() * WORDS.length)])])
        fragment.insert(index, [element])
      })
    } else if (roll < 0.05 && fragment.length > 3) {
      // Delete a whole top-level paragraph, never the last block.
      const index = Math.floor(rnd() * (fragment.length - 1))
      const node = fragment.get(index)
      if (node instanceof Y.XmlElement && node.nodeName === 'paragraph') fragment.delete(index, 1)
    } else if (texts.length > 0) {
      const target = texts[Math.floor(rnd() * texts.length)]
      const raw = rawText(target)
      const length = raw.length
      const range = (maxLength: number) => {
        const at = boundary(raw, Math.floor(rnd() * (length - 1)))
        const end = boundary(raw, Math.min(at + 1 + Math.floor(rnd() * maxLength), length))
        return [at, Math.max(end - at, 0)] as const
      }
      if (roll < 0.25 && length > 2) {
        const [at, count] = range(4)
        if (count > 0) target.delete(at, count)
      } else if (mix === 'all' && roll < 0.32 && length > 2) {
        const [at, count] = range(3)
        if (count > 0) target.format(at, count, { [MARKS[Math.floor(rnd() * MARKS.length)]]: {} })
      } else {
        target.insert(boundary(raw, Math.floor(rnd() * (length + 1))), WORDS[Math.floor(rnd() * WORDS.length)])
      }
    }

    // Sync sometimes, so clients really edit concurrently on stale state.
    if (rnd() < 0.3) flush(client)
  }
  for (const client of clients) flush(client)

  const final = clients[0].doc
  for (const other of clients.slice(1)) {
    if (plainText(other.doc) !== plainText(final)) throw new Error('clients did not converge')
  }
  return { log, final }
}

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
  const doc = prosemirrorJSONToYDoc(schema, { type: 'doc', content: [paragraph(text('a😀b'))] }, FIELD)
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
