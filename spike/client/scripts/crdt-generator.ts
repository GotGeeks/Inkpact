// Random concurrent TipTap editing sessions written by real Yjs: the generator behind the 0.4 fixtures
// (crdt-fixtures.ts) and the 0.5 compaction stress streams (compaction-stream.ts).
import * as Y from 'yjs'
import { getSchema } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { prosemirrorJSONToYXmlFragment } from '@tiptap/y-tiptap'
import { plainText } from './crdt-fixtures-text.ts'

export const schema = getSchema([StarterKit])
export const FIELD = 'default' // the fragment name TipTap's Collaboration extension uses

export const text = (value: string, marks?: string[]) => ({
  type: 'text',
  text: value,
  ...(marks ? { marks: marks.map((type) => ({ type })) } : {}),
})
export const paragraph = (...content: object[]) => ({ type: 'paragraph', content })

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
export type EditMix = 'all' | 'no-format' | 'insert-only'

export function concurrentEdits(edits: number, seed: number, mix: EditMix) {
  const rnd = random(seed)
  // A fixed client id for the base document (prosemirrorJSONToYDoc picks a random one), so the same
  // seed always gives byte-identical updates.
  const base = new Y.Doc()
  base.clientID = 999
  prosemirrorJSONToYXmlFragment(schema, richDocument, base.getXmlFragment(FIELD))
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
