import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import { plainText } from '../src/text.ts'

/** A block element with its text, in the shape TipTap's collaboration extension writes. */
function block(parent: Y.XmlFragment | Y.XmlElement, name: string, content?: string): Y.XmlElement {
  const element = new Y.XmlElement(name)
  parent.insert(parent.length, [element])
  if (content !== undefined) {
    const text = new Y.XmlText()
    element.insert(0, [text])
    text.insert(0, content)
  }
  return element
}

function documentWith(build: (root: Y.XmlFragment) => void): Y.Doc {
  const doc = new Y.Doc()
  build(doc.getXmlFragment('default'))
  return doc
}

describe('plainText', () => {
  it('puts each paragraph on its own line', () => {
    const doc = documentWith((root) => {
      block(root, 'paragraph', 'one')
      block(root, 'paragraph', 'two')
    })

    expect(plainText(doc)).toBe('one\ntwo')
  })

  it('reads headings like paragraphs', () => {
    const doc = documentWith((root) => {
      block(root, 'heading', 'Title')
      block(root, 'paragraph', 'body')
    })

    expect(plainText(doc)).toBe('Title\nbody')
  })

  it('puts every item of a nested list on its own line', () => {
    const doc = documentWith((root) => {
      const list = block(root, 'bulletList')
      const first = block(list, 'listItem')
      block(first, 'paragraph', 'a')
      const second = block(list, 'listItem')
      block(second, 'paragraph', 'b')
      const inner = block(second, 'bulletList')
      block(block(inner, 'listItem'), 'paragraph', 'c')
      block(root, 'paragraph', 'after')
    })

    expect(plainText(doc)).toBe('a\nb\nc\nafter')
  })

  it('puts every cell of a table on its own line, in order', () => {
    const doc = documentWith((root) => {
      const table = block(root, 'table')
      for (const row of [
        ['a1', 'b1'],
        ['a2', 'b2'],
      ]) {
        const tableRow = block(table, 'tableRow')
        for (const cell of row) block(block(tableRow, 'tableCell'), 'paragraph', cell)
      }
    })

    expect(plainText(doc)).toBe('a1\nb1\na2\nb2')
  })

  it('keeps the lines of a code block', () => {
    const doc = documentWith((root) => {
      block(root, 'paragraph', 'before')
      block(root, 'codeBlock', 'const a = 1\nconst b = 2')
    })

    expect(plainText(doc)).toBe('before\nconst a = 1\nconst b = 2')
  })

  it('turns a hard break inside a paragraph into a line break', () => {
    const doc = documentWith((root) => {
      const paragraph = block(root, 'paragraph')
      const first = new Y.XmlText()
      paragraph.insert(0, [first])
      first.insert(0, 'a')
      paragraph.insert(1, [new Y.XmlElement('hardBreak')])
      const second = new Y.XmlText()
      paragraph.insert(2, [second])
      second.insert(0, 'b')
    })

    expect(plainText(doc)).toBe('a\nb')
  })

  it('does not split a word that has a mark inside it', () => {
    const doc = documentWith((root) => {
      const paragraph = block(root, 'paragraph', 'hello')
      ;(paragraph.get(0) as Y.XmlText).format(2, 2, { bold: true })
    })

    expect(plainText(doc)).toBe('hello')
  })

  it('leaves out attributes and embeds', () => {
    const doc = documentWith((root) => {
      const paragraph = block(root, 'paragraph', 'see the docs')
      const text = paragraph.get(0) as Y.XmlText
      text.format(8, 4, { link: { href: 'https://example.com/secret' } })
      text.insertEmbed(3, { image: 'data:image/png;base64,AAAA' })
    })

    expect(plainText(doc)).toBe('see the docs')
  })

  it('keeps an emoji whole', () => {
    const doc = documentWith((root) => block(root, 'paragraph', 'a😀b'))

    expect(plainText(doc)).toBe('a😀b')
  })

  it('shows half of a deleted emoji as the replacement character, as Yjs does', () => {
    const doc = documentWith((root) => {
      const paragraph = block(root, 'paragraph', 'a😀b')
      // Index 2 is the low half of the surrogate pair.
      ;(paragraph.get(0) as Y.XmlText).delete(2, 1)
    })

    expect(plainText(doc)).toBe('a�b')
  })

  it('trims trailing spaces, squeezes empty lines to one and trims the whole', () => {
    const doc = documentWith((root) => {
      block(root, 'paragraph')
      block(root, 'paragraph', 'a   ')
      block(root, 'paragraph')
      block(root, 'paragraph')
      block(root, 'paragraph')
      block(root, 'paragraph', 'b')
      block(root, 'paragraph')
    })

    expect(plainText(doc)).toBe('a\n\nb')
  })

  it('has no text for an empty document', () => {
    expect(plainText(new Y.Doc())).toBe('')
  })

  it('has no text for an empty fragment', () => {
    expect(plainText(documentWith(() => {}))).toBe('')
  })

  it('has no text for a document without the default fragment', () => {
    const doc = new Y.Doc()
    doc.getMap('settings').set('title', 'not text')
    doc.getText('notes').insert(0, 'not in the field the editor uses')

    expect(plainText(doc)).toBe('')
    expect(doc.share.has('default')).toBe(false)
  })

  it('has no text when the default field holds something else than an XML tree', () => {
    const doc = new Y.Doc()
    doc.getText('default').insert(0, 'plain text')
    const copy = new Y.Doc()
    Y.applyUpdate(copy, Y.encodeStateAsUpdate(doc))

    expect(plainText(copy)).toBe('')
  })

  it('copes with a page nested far deeper than the call stack', () => {
    const doc = new Y.Doc()
    // One transaction: Yjs walks up the tree once per transaction, so one per level would take minutes.
    doc.transact(() => {
      let parent: Y.XmlFragment | Y.XmlElement = doc.getXmlFragment('default')
      for (let depth = 0; depth < 50_000; depth++) parent = block(parent, 'blockquote')
      const text = new Y.XmlText()
      parent.insert(0, [text])
      text.insert(0, 'deep')
    })

    expect(plainText(doc)).toBe('deep')
  })
})
