import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import { FIELD, MARK_ATTRIBUTES, NODE_ATTRIBUTES, buildDocument } from '../src/build.ts'
import { OP_READ, handle } from '../src/ops.ts'
import { readDocument } from '../src/read.ts'
import { replayed, text as decode, typed } from './helpers.ts'

const doc = (...content: unknown[]) => ({ type: 'doc', content })
const paragraph = (...content: unknown[]) => ({ type: 'paragraph', content })
const text = (value: string, ...marks: unknown[]) => ({
  type: 'text',
  text: value,
  ...(marks.length > 0 ? { marks } : {}),
})

const readBack = (content: unknown) => readDocument(replayed([buildDocument(content)]))

describe('readDocument', () => {
  it('gives back an empty document for a page that has only its first paragraph', () => {
    expect(readDocument(replayed([buildDocument(doc())]))).toEqual({
      document: { type: 'doc', content: [{ type: 'paragraph' }] },
      skipped: 0,
    })
  })

  it('gives back an empty document for a page without the fragment', () => {
    expect(readDocument(new Y.Doc())).toEqual({ document: { type: 'doc' }, skipped: 0 })
  })

  it('gives back what build was given, for every node of the schema with its attributes', () => {
    const content = doc(
      { type: 'heading', attrs: { level: 3 }, content: [text('Heading')] },
      paragraph(text('One'), { type: 'hardBreak' }, text('two')),
      { type: 'blockquote', content: [paragraph(text('quoted'))] },
      {
        type: 'bulletList',
        content: [
          { type: 'listItem', content: [paragraph(text('a'))] },
          { type: 'listItem', content: [paragraph(text('b'))] },
        ],
      },
      {
        type: 'orderedList',
        attrs: { start: 3, type: '1' },
        content: [{ type: 'listItem', content: [paragraph(text('c'))] }],
      },
      {
        type: 'taskList',
        content: [
          { type: 'taskItem', attrs: { checked: true }, content: [paragraph(text('done'))] },
          { type: 'taskItem', attrs: { checked: false }, content: [paragraph(text('todo'))] },
        ],
      },
      { type: 'codeBlock', attrs: { language: 'ts' }, content: [text('const x = 1')] },
      { type: 'horizontalRule' },
      {
        type: 'table',
        content: [
          {
            type: 'tableRow',
            content: [
              {
                type: 'tableHeader',
                attrs: { colspan: 2, rowspan: 1, colwidth: [100, 200], align: 'center' },
                content: [paragraph(text('head'))],
              },
            ],
          },
          {
            type: 'tableRow',
            content: [{ type: 'tableCell', content: [paragraph(text('cell'))] }],
          },
        ],
      },
      { type: 'image', attrs: { attachmentId: 'a1', alt: 'a picture' } },
      paragraph({ type: 'attachment', attrs: { attachmentId: 'a2', fileName: 'x.pdf', size: 10 } }),
      paragraph(text('end')),
    )

    expect(readBack(content)).toEqual({ document: content, skipped: 0 })
  })

  it('gives back every mark with its attributes, in one order whatever order they were written in', () => {
    const link = { type: 'link', attrs: { href: 'https://example.com/', title: 'Example' } }
    const content = doc(
      paragraph(
        text(
          'all',
          { type: 'code' },
          link,
          { type: 'strike' },
          { type: 'italic' },
          { type: 'bold' },
          { type: 'underline' },
        ),
        text(' plain '),
        text('bold', { type: 'bold' }),
      ),
    )

    const read = readBack(content).document as {
      content: { content: { marks?: { type: string }[] }[] }[]
    }

    expect(read.content[0]!.content[0]!.marks!.map((mark) => mark.type)).toEqual([
      'link',
      'underline',
      'bold',
      'italic',
      'strike',
      'code',
    ])
    expect(read.content[0]!.content.map((node) => (node as { text: string }).text)).toEqual([
      'all',
      ' plain ',
      'bold',
    ])
    expect(read.content[0]!.content[0]!.marks![0]).toEqual(link)
  })

  it('reads a paragraph that was edited by typing, as the editor made it', () => {
    const updates = typed(
      (ydoc) => {
        const paragraphElement = new Y.XmlElement('paragraph')
        const textNode = new Y.XmlText()
        paragraphElement.insert(0, [textNode])
        ydoc.getXmlFragment(FIELD).insert(0, [paragraphElement])
        textNode.insert(0, 'Hello')
      },
      (ydoc) => {
        const textNode = (ydoc.getXmlFragment(FIELD).get(0) as Y.XmlElement).get(0) as Y.XmlText
        textNode.insert(5, ' world', { bold: {} })
        textNode.insert(0, 'Oh, ')
      },
    )

    expect(readDocument(replayed(updates)).document).toEqual(
      doc(paragraph(text('Oh, Hello'), text(' world', { type: 'bold' }))),
    )
  })

  it('leaves out and counts what the schema does not have', () => {
    const ydoc = new Y.Doc()
    const fragment = ydoc.getXmlFragment(FIELD)
    const known = new Y.XmlElement('heading')
    known.setAttribute('level', 2 as never)
    known.setAttribute('unknownAttribute', 'x' as never)
    const knownText = new Y.XmlText()
    known.insert(0, [knownText])
    knownText.insert(0, 'kept', {
      bold: {},
      sparkle: { colour: 'red' },
      link: { href: 'https://x.test/', evil: 'y' },
    })
    const unknown = new Y.XmlElement('callout')
    unknown.insert(0, [new Y.XmlElement('paragraph')])
    fragment.insert(0, [known, unknown])
    knownText.insertEmbed(4, { image: 'x' })

    const { document, skipped } = readDocument(ydoc)

    expect(document).toEqual(
      doc({
        type: 'heading',
        attrs: { level: 2 },
        content: [
          text('kept', { type: 'link', attrs: { href: 'https://x.test/' } }, { type: 'bold' }),
        ],
      }),
    )
    // The unknown attribute, the unknown mark, the unknown mark's... the mark attribute, the embed, the unknown node.
    expect(skipped).toBe(5)
  })

  it('follows a page nested deeper than the editor builds, and stops at a bound without exhausting the stack', () => {
    const ydoc = new Y.Doc()
    let parent: Y.XmlElement | Y.XmlFragment = ydoc.getXmlFragment(FIELD)
    for (let i = 0; i < 5000; i++) {
      const quote = new Y.XmlElement('blockquote')
      parent.insert(0, [quote])
      parent = quote
    }

    const { skipped } = readDocument(ydoc)

    expect(skipped).toBeGreaterThan(0)
  })

  it('names the same nodes and marks as the schema it is the inverse of', () => {
    // Everything the schema lists reads back: no name is missing from the walk.
    for (const name of Object.keys(NODE_ATTRIBUTES)) {
      const ydoc = new Y.Doc()
      ydoc.getXmlFragment(FIELD).insert(0, [new Y.XmlElement(name)])
      expect(readDocument(ydoc)).toEqual({ document: doc({ type: name }), skipped: 0 })
    }
    for (const name of Object.keys(MARK_ATTRIBUTES)) {
      const ydoc = new Y.Doc()
      const paragraphElement = new Y.XmlElement('paragraph')
      const textNode = new Y.XmlText()
      paragraphElement.insert(0, [textNode])
      ydoc.getXmlFragment(FIELD).insert(0, [paragraphElement])
      textNode.insert(0, 'x', { [name]: {} })
      expect(readDocument(ydoc).skipped).toBe(0)
    }
  })
})

describe('the read operation', () => {
  it('answers the JSON and the count, for a snapshot and updates replayed in order', () => {
    const first = buildDocument(doc(paragraph(text('Hello'))))
    const state = Y.encodeStateAsUpdate(replayed([first]))

    const [json, skipped] = handle(OP_READ, [state])

    expect(JSON.parse(decode(json))).toEqual(doc(paragraph(text('Hello'))))
    expect(decode(skipped)).toBe('0')
  })

  it('answers an empty document for no inputs and refuses an input that is not an update', () => {
    expect(JSON.parse(decode(handle(OP_READ, [])[0]))).toEqual({ type: 'doc' })
    expect(() => handle(OP_READ, [new Uint8Array([1, 2, 3])])).toThrow()
  })
})
