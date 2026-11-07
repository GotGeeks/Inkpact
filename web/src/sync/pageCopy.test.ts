import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import { applyFirstParagraph, FIELD } from '../editor/firstParagraph'
import { copyUpdate, newPageId, toBase64 } from './pageCopy'

function page(...texts: string[]): Uint8Array {
  const doc = new Y.Doc()
  applyFirstParagraph(doc)
  const fragment = doc.getXmlFragment(FIELD)
  doc.transact(() => {
    fragment.delete(0, fragment.length)
    for (const text of texts) {
      const paragraph = new Y.XmlElement('paragraph')
      const content = new Y.XmlText()
      content.insert(0, text)
      paragraph.insert(0, [content])
      fragment.push([paragraph])
    }
  })
  return Y.encodeStateAsUpdate(doc)
}

const blocksOf = (update: Uint8Array) => {
  const doc = new Y.Doc()
  Y.applyUpdate(doc, update)
  return doc
    .getXmlFragment(FIELD)
    .toArray()
    .map((node) => node.toString())
}

describe('copyUpdate', () => {
  it('makes a new page with the blocks of the page it copies', () => {
    expect(blocksOf(copyUpdate(page('one', 'two')))).toEqual(blocksOf(page('one', 'two')))
  })

  it('starts from the first paragraph, so a client applying it again changes nothing', () => {
    const copy = new Y.Doc()
    Y.applyUpdate(copy, copyUpdate(page('one')))
    const before = Y.encodeStateAsUpdate(copy)

    applyFirstParagraph(copy)

    expect(Y.encodeStateAsUpdate(copy)).toEqual(before)
  })
})

describe('copyUpdate of a large page', () => {
  it('is only a little larger than the page it copies, within the eighth more that the server takes', () => {
    // Several MiB of text in many blocks, like an imported note of a few thousand paragraphs.
    const paragraph = 'The quick brown fox jumps over the lazy dog. '.repeat(40)
    const original = page(...Array.from({ length: 3000 }, (_, i) => `${i} ${paragraph}`))
    expect(original.length).toBeGreaterThan(3 * 1024 * 1024)

    const copy = copyUpdate(original)

    // Written again from the blocks, so not the same size (here 0.14 % more); the server takes a copy up to an eighth above
    // the limit of a page (SyncOptions.MaxCopyBytes), so a page right at the limit still gets its copy.
    expect(copy.length).toBeLessThan(original.length * 1.02)
    expect(blocksOf(copy)).toEqual(blocksOf(original))
  })
})

describe('toBase64', () => {
  it('encodes bytes, also more than fit in one argument list', () => {
    const bytes = Uint8Array.from({ length: 100_000 }, (_, i) => i % 256)

    expect(Uint8Array.from(atob(toBase64(bytes)), (c) => c.charCodeAt(0))).toEqual(bytes)
  })

  it('encodes a page of several MiB', () => {
    const bytes = new Uint8Array(9 * 1024 * 1024)
    for (let i = 0; i < bytes.length; i++) bytes[i] = (i * 31) % 256

    expect(toBase64(bytes)).toBe(Buffer.from(bytes).toString('base64'))
  })
})

describe('newPageId', () => {
  it('makes version 4 UUIDs, different each time', () => {
    const ids = Array.from({ length: 50 }, newPageId)

    for (const id of ids) {
      expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    }
    expect(new Set(ids).size).toBe(50)
  })
})
