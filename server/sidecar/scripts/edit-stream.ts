// Writes a seeded stream of real Yjs updates for the server's compaction tests: three clients edit one TipTap-shaped
// document at the same time and sync now and then, and the updates are recorded in the order the server would receive
// them. A few are delivered out of causal order. Test support only; it is not shipped and not part of the protocol.
//
//   node scripts/edit-stream.ts <edits> <seed> <file>
//
// File format: repeated [uint32 little-endian length][Yjs v1 update].
import { writeFileSync } from 'node:fs'
import * as Y from 'yjs'

const [edits, seed, file] = process.argv.slice(2)
if (!edits || !seed || !file)
  throw new Error('Usage: node scripts/edit-stream.ts <edits> <seed> <file>')

/** mulberry32: small, and the same numbers for the same seed on every machine. */
function random(seed: number): () => number {
  let state = seed >>> 0
  return () => {
    state = (state + 0x6d2b79f5) >>> 0
    let t = state
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

const FIELD = 'default' // the fragment TipTap's collaboration extension uses
const WORDS = ['kaas', 'appel', 'thee', 'notitie', 'Ünïcode', '日本', '😀', '👩‍💻', ' ', ' ', ', ']
const MARKS = ['bold', 'italic', 'strike', 'code']

const rnd = random(Number(seed))
const pick = <T>(items: readonly T[]): T => items[Math.floor(rnd() * items.length)] as T

/** Moves a UTF-16 index off the middle of a surrogate pair: an editor never splits an emoji. */
function boundary(value: string, index: number): number {
  const code = value.charCodeAt(index)
  return index > 0 && index < value.length && code >= 0xdc00 && code <= 0xdfff ? index - 1 : index
}

/** The characters of an XmlText without the mark tags toString() adds. */
const rawText = (node: Y.XmlText) =>
  (node.toDelta() as { insert: unknown }[])
    .map((op) => (typeof op.insert === 'string' ? op.insert : ''))
    .join('')

function textsOf(fragment: Y.XmlFragment): Y.XmlText[] {
  const found: Y.XmlText[] = []
  const walk = (node: Y.XmlFragment | Y.XmlElement) => {
    for (const child of node.toArray()) {
      if (child instanceof Y.XmlText) found.push(child)
      else if (child instanceof Y.XmlElement) walk(child)
    }
  }
  walk(fragment)
  return found
}

function paragraph(text: string): Y.XmlElement {
  const element = new Y.XmlElement('paragraph')
  element.insert(0, [new Y.XmlText(text)])
  return element
}

// A fixed client id for the base document, so one seed always gives the same bytes.
const base = new Y.Doc()
base.clientID = 999
base
  .getXmlFragment(FIELD)
  .insert(0, [paragraph('Boodschappen'), paragraph('kaas, appel, thee'), paragraph('')])
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

function flush(sender: (typeof clients)[number]) {
  for (const update of sender.outbox.splice(0)) {
    log.push(update)
    for (const other of clients) if (other !== sender) Y.applyUpdate(other.doc, update, 'remote')
  }
}

for (let i = 0; i < Number(edits); i++) {
  const client = pick(clients)
  const fragment = client.doc.getXmlFragment(FIELD)
  const texts = textsOf(fragment)
  const roll = rnd()

  if (roll < 0.04) {
    // A new paragraph, which is how a split starts: at a random top-level position.
    fragment.insert(Math.floor(rnd() * (fragment.length + 1)), [paragraph(pick(WORDS))])
  } else if (roll < 0.07 && fragment.length > 2) {
    // A join: a whole top-level paragraph goes, never the last blocks.
    fragment.delete(Math.floor(rnd() * fragment.length), 1)
  } else if (texts.length > 0) {
    const target = pick(texts)
    const raw = rawText(target)
    const length = raw.length
    const range = (most: number) => {
      const at = boundary(raw, Math.floor(rnd() * Math.max(length - 1, 1)))
      const end = boundary(raw, Math.min(at + 1 + Math.floor(rnd() * most), length))
      return [at, Math.max(end - at, 0)] as const
    }
    if (roll < 0.28 && length > 2) {
      const [at, count] = range(4)
      if (count > 0) target.delete(at, count)
    } else if (roll < 0.36 && length > 2) {
      const [at, count] = range(3)
      if (count > 0) target.format(at, count, { [pick(MARKS)]: {} })
    } else {
      target.insert(boundary(raw, Math.floor(rnd() * (length + 1))), pick(WORDS))
    }
  }

  // Sync sometimes, so the clients really edit on stale state.
  if (rnd() < 0.3) flush(client)
}
for (const client of clients) flush(client)

// Delivery out of causal order: swap some neighbours, never the base update. A later update that arrives first waits in
// Yjs as a pending struct until the earlier one comes.
for (let i = 2; i < log.length; i++) {
  if (rnd() < 0.04) {
    const earlier = log[i - 1] as Uint8Array
    log[i - 1] = log[i] as Uint8Array
    log[i] = earlier
    i++
  }
}

const parts: Buffer[] = []
for (const update of log) {
  const length = Buffer.alloc(4)
  length.writeUInt32LE(update.length)
  parts.push(length, Buffer.from(update))
}
writeFileSync(file, Buffer.concat(parts))
console.log(`${log.length} updates -> ${file}`)
