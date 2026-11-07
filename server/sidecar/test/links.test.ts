import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import { OP_COMPACT, OP_LINKS, handle } from '../src/ops.ts'
import { pageIdOfHref, pageLinks } from '../src/links.ts'
import { text, typed } from './helpers.ts'

const A = '0a1b2c3d-0000-4000-8000-00000000000a'
const B = '0a1b2c3d-0000-4000-8000-00000000000b'
const C = '0a1b2c3d-0000-4000-8000-00000000000c'

/** A paragraph in the shape TipTap writes it, whose text carries the given marks (an attribute `link` for a link mark). */
function paragraph(
  parent: Y.XmlFragment | Y.XmlElement,
  pieces: [string, Record<string, unknown>?][],
): Y.XmlElement {
  const element = new Y.XmlElement('paragraph')
  parent.insert(parent.length, [element])
  const content = new Y.XmlText()
  element.insert(0, [content])
  let at = 0
  for (const [piece, attributes] of pieces) {
    content.insert(at, piece, attributes)
    at += piece.length
  }
  return element
}

const link = (href: string) => ({ link: { href } })

function documentWith(build: (root: Y.XmlFragment) => void): Y.Doc {
  const doc = new Y.Doc()
  build(doc.getXmlFragment('default'))
  return doc
}

describe('pageIdOfHref', () => {
  it('reads /p/<id>, the address the editor and the import write', () => {
    expect(pageIdOfHref(`/p/${A}`)).toBe(A)
  })

  it.each([
    ['a query', `/p/${A}?x=1`],
    ['a fragment', `/p/${A}#top`],
    ['a longer path', `/p/${A}/edit`],
    ['another path', `/page/${A}`],
    ['upper case', `/p/${A.toUpperCase()}`],
    ['no id', '/p/'],
    ['a short id', '/p/1234'],
    // The server cannot tell its own host names from another installation's; the editor stores its own as /p/<id>.
    ['a full address', `https://notes.example.com/p/${A}`],
    ['a full address on the LAN', `http://192.168.1.10/p/${A}`],
    ['mail', 'mailto:a@example.com'],
    ['javascript', `javascript:alert("/p/${A}")`],
    ['a data address', `data:text/html,/p/${A}`],
    ['a relative address', `p/${A}`],
    ['nothing', ''],
  ])('does not read %s', (_name, href) => {
    expect(pageIdOfHref(href)).toBeNull()
  })
})

describe('pageLinks', () => {
  it('finds the pages the link marks of a page point to, once each and sorted', () => {
    const doc = documentWith((root) => {
      paragraph(root, [['see '], ['b', link(`/p/${B}`)], [' and '], ['a', link(`/p/${A}`)]])
      paragraph(root, [['b again', link(`/p/${B}`)]])
    })

    expect(pageLinks(doc)).toEqual([A, B])
  })

  it('finds links in lists, quotes and tables', () => {
    const doc = documentWith((root) => {
      const list = new Y.XmlElement('bulletList')
      root.insert(root.length, [list])
      const item = new Y.XmlElement('listItem')
      list.insert(0, [item])
      paragraph(item, [['a', link(`/p/${A}`)]])
      const quote = new Y.XmlElement('blockquote')
      root.insert(root.length, [quote])
      paragraph(quote, [['b', link(`/p/${B}`)]])
      const table = new Y.XmlElement('table')
      root.insert(root.length, [table])
      const row = new Y.XmlElement('tableRow')
      table.insert(0, [row])
      const cell = new Y.XmlElement('tableCell')
      row.insert(0, [cell])
      paragraph(cell, [['c', link(`/p/${C}`)]])
    })

    expect(pageLinks(doc)).toEqual([A, B, C])
  })

  it('leaves out links that are not to a page of the app, and text that only looks like one', () => {
    const doc = documentWith((root) => {
      paragraph(root, [
        ['web', link('https://example.com/')],
        ['mail', link('mailto:a@example.com')],
        ['query', link(`/p/${A}?x=1`)],
        [`/p/${B}`],
        ['bold', { bold: true }],
      ])
    })

    expect(pageLinks(doc)).toEqual([])
  })

  it('does not find a link that was removed', () => {
    const doc = documentWith((root) => {
      paragraph(root, [['gone', link(`/p/${A}`)]])
    })
    const content = doc.getXmlFragment('default').get(0) as Y.XmlElement
    ;(content.get(0) as Y.XmlText).format(0, 4, { link: null })

    expect(pageLinks(doc)).toEqual([])
  })

  it('has none for a page nobody wrote to', () => {
    expect(pageLinks(new Y.Doc())).toEqual([])
  })

  it('follows a deeply nested page without exhausting the call stack', () => {
    const doc = documentWith((root) => {
      let parent: Y.XmlFragment | Y.XmlElement = root
      for (let depth = 0; depth < 5000; depth++) {
        const quote = new Y.XmlElement('blockquote')
        parent.insert(parent.length, [quote])
        parent = quote
      }
      paragraph(parent, [['deep', link(`/p/${A}`)]])
    })

    expect(pageLinks(doc)).toEqual([A])
  })
})

describe('the links-only operation', () => {
  it('answers with the links of the replayed inputs alone, as the compaction would', () => {
    const updates = typed(
      (doc) => paragraph(doc.getXmlFragment('default'), [['a', link(`/p/${A}`)]]),
      (doc) => paragraph(doc.getXmlFragment('default'), [['b', link(`/p/${B}`)]]),
    )

    const answer = handle(OP_LINKS, updates)

    expect(answer).toHaveLength(1)
    expect(text(answer[0])).toBe(`${A}\n${B}`)
    expect(text(answer[0])).toBe(text(handle(OP_COMPACT, updates)[5]))
  })

  it('answers with an empty part for a page without links, and for no inputs at all', () => {
    expect(
      text(
        handle(
          OP_LINKS,
          typed((doc) => doc.getMap('m').set('k', 'v')),
        )[0],
      ),
    ).toBe('')
    expect(text(handle(OP_LINKS, [])[0])).toBe('')
  })
})

describe('the links of a compaction', () => {
  it('are the sixth part of the answer, one id on each line', () => {
    const updates = typed((doc) => {
      const root = doc.getXmlFragment('default')
      paragraph(root, [
        ['b', link(`/p/${B}`)],
        ['a', link(`/p/${A}`)],
      ])
    })

    const answer = handle(OP_COMPACT, updates)

    expect(answer).toHaveLength(6)
    expect(text(answer[5])).toBe(`${A}\n${B}`)
  })

  it('are an empty part when the page links to nothing', () => {
    expect(
      text(
        handle(
          OP_COMPACT,
          typed((doc) => doc.getMap('m').set('k', 'v')),
        )[5],
      ),
    ).toBe('')
  })

  it('are those of the whole page when the inputs are a snapshot and the updates after it', () => {
    const updates = typed(
      (doc) => paragraph(doc.getXmlFragment('default'), [['a', link(`/p/${A}`)]]),
      (doc) => paragraph(doc.getXmlFragment('default'), [['b', link(`/p/${B}`)]]),
    )
    const [snapshot] = handle(OP_COMPACT, updates.slice(0, 1))

    expect(text(handle(OP_COMPACT, [snapshot!, ...updates.slice(1)])[5])).toBe(`${A}\n${B}`)
  })
})
