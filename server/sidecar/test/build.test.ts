import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import {
  FIELD,
  FIRST_PARAGRAPH_CLIENT_ID,
  InvalidContentError,
  MARK_ATTRIBUTES,
  NODE_ATTRIBUTES,
  buildDocument,
  replaceDocument,
} from '../src/build.ts'
import { OP_BUILD, OP_REPLACE, handle } from '../src/ops.ts'
import { plainText } from '../src/text.ts'
import { fingerprintOf, replayed } from './helpers.ts'

const doc = (...content: unknown[]) => ({ type: 'doc', content })
const paragraph = (...content: unknown[]) => ({ type: 'paragraph', content })
const text = (value: string, ...marks: unknown[]) => ({
  type: 'text',
  text: value,
  ...(marks.length > 0 ? { marks } : {}),
})

/** The first-paragraph update, built the way the clients build it (web/src/editor/firstParagraph.ts). */
function firstParagraph(): Uint8Array {
  const first = new Y.Doc()
  first.clientID = FIRST_PARAGRAPH_CLIENT_ID
  const paragraph = new Y.XmlElement('paragraph')
  paragraph.insert(0, [new Y.XmlText()])
  first.getXmlFragment(FIELD).insert(0, [paragraph])
  return Y.encodeStateAsUpdate(first)
}

const fragmentOf = (update: Uint8Array) => replayed([update]).getXmlFragment(FIELD)

describe('buildDocument', () => {
  it('keeps the first paragraph as it is for an empty note', () => {
    expect(buildDocument(doc())).toEqual(firstParagraph())
    expect(buildDocument({ type: 'doc' })).toEqual(firstParagraph())
  })

  it('writes elements named after their type, with their attributes, and leaves out null ones', () => {
    const fragment = fragmentOf(
      buildDocument(
        doc(
          { type: 'heading', attrs: { level: 2 }, content: [text('Title')] },
          { type: 'codeBlock', attrs: { language: null }, content: [text('x')] },
          { type: 'orderedList', attrs: { start: 3, type: null }, content: [] },
        ),
      ),
    )

    const [heading, code, list] = fragment.toArray() as Y.XmlElement[]
    // The last is the empty paragraph the editor would otherwise add.
    expect(fragment.length).toBe(4)
    expect(heading!.nodeName).toBe('heading')
    expect(heading!.getAttributes()).toEqual({ level: 2 })
    expect(code!.getAttributes()).toEqual({})
    expect(list!.getAttributes()).toEqual({ start: 3 })
  })

  it('writes adjacent text nodes as one text, each mark as a format attribute holding its attributes or {}', () => {
    const fragment = fragmentOf(
      buildDocument(
        doc(
          paragraph(
            text('plain '),
            text('bold', { type: 'bold' }),
            text('link', { type: 'link', attrs: { href: 'https://example.com', target: null } }),
          ),
        ),
      ),
    )

    const [first] = fragment.toArray() as Y.XmlElement[]
    expect(first!.length).toBe(1)
    expect((first!.get(0) as Y.XmlText).toDelta()).toEqual([
      { insert: 'plain ' },
      { insert: 'bold', attributes: { bold: {} } },
      { insert: 'link', attributes: { link: { href: 'https://example.com' } } },
    ])
  })

  it('splits text around an inline node into separate texts', () => {
    const fragment = fragmentOf(
      buildDocument(doc(paragraph(text('a'), { type: 'hardBreak' }, text('b')))),
    )

    const [first] = fragment.toArray() as Y.XmlElement[]
    expect(first!.toArray().map((child) => child.constructor.name)).toEqual([
      'YXmlText',
      'YXmlElement',
      'YXmlText',
    ])
  })

  it('starts from the first paragraph: it is deleted, and the content comes after it', () => {
    const update = buildDocument(doc(paragraph(text('hello'))))
    const result = replayed([update])

    expect(result.getXmlFragment(FIELD).length).toBe(1)
    expect(plainText(result)).toBe('hello')
    // The first paragraph's items are there, and deleted.
    expect(
      Y.decodeStateVector(Y.encodeStateVector(result)).get(FIRST_PARAGRAPH_CLIENT_ID),
    ).toBeGreaterThan(0)
  })

  it('ends with an empty paragraph unless the content already does, as the editor would make it', () => {
    const ending = (content: unknown[]) =>
      (fragmentOf(buildDocument(doc(...content))).toArray() as Y.XmlElement[]).map(
        (node) => node.nodeName,
      )

    expect(ending([paragraph(text('a'))])).toEqual(['paragraph'])
    expect(ending([{ type: 'horizontalRule' }])).toEqual(['horizontalRule', 'paragraph'])
    expect(ending([{ type: 'bulletList' }, paragraph()])).toEqual(['bulletList', 'paragraph'])
  })

  it('does not bring the first paragraph back when a client applies it again', () => {
    const update = buildDocument(doc(paragraph(text('hello'))))
    const result = replayed([update])
    const before = Y.encodeStateAsUpdate(result)

    Y.applyUpdate(result, firstParagraph())

    expect(Y.encodeStateAsUpdate(result)).toEqual(before)
    expect(result.getXmlFragment(FIELD).length).toBe(1)
    expect(plainText(result)).toBe('hello')
  })

  it('gives the same document when the update is applied twice', () => {
    const update = buildDocument(doc(paragraph(text('hello'))))
    const once = replayed([update])
    const twice = replayed([update, update])

    expect(Y.encodeStateAsUpdate(twice)).toEqual(Y.encodeStateAsUpdate(once))
  })

  it('accepts every node and mark in its lists', () => {
    for (const name of Object.keys(NODE_ATTRIBUTES)) {
      expect(() => buildDocument(doc({ type: name }))).not.toThrow()
    }
    for (const name of Object.keys(MARK_ATTRIBUTES)) {
      expect(() => buildDocument(doc(paragraph(text('x', { type: name }))))).not.toThrow()
    }
  })

  describe('refuses', () => {
    it.each([
      ['an unknown node', doc({ type: 'script' })],
      ['a node named like an inherited property', doc({ type: 'constructor' })],
      ['a node with an unknown attribute', doc({ type: 'paragraph', attrs: { onclick: 'x' } })],
      ['an unknown mark', doc(paragraph(text('x', { type: 'highlight' })))],
      [
        'a mark with an unknown attribute',
        doc(paragraph(text('x', { type: 'bold', attrs: { a: 1 } }))),
      ],
      ['an attribute that is an object', doc({ type: 'heading', attrs: { level: { a: 1 } } })],
      ['an empty text', doc(paragraph(text('')))],
      ['a text without text', doc(paragraph({ type: 'text' }))],
      ['content that is not a doc', { type: 'paragraph' }],
      ['content that is not an object', 'hello'],
      ['content that is not a list', { type: 'doc', content: 'x' }],
    ])('%s', (_name, content) => {
      expect(() => buildDocument(content)).toThrow(InvalidContentError)
    })

    it('nesting that is too deep', () => {
      let nested: unknown = { type: 'paragraph' }
      for (let i = 0; i < 200; i++) nested = { type: 'blockquote', content: [nested] }

      expect(() => buildDocument(doc(nested))).toThrow(InvalidContentError)
    })
  })
})

describe('the build operation', () => {
  it('answers with the update for the JSON it was sent', () => {
    const json = Buffer.from(JSON.stringify(doc(paragraph(text('via the wire')))), 'utf8')

    const [update] = handle(OP_BUILD, [json])

    expect(plainText(replayed([update!]))).toBe('via the wire')
  })

  it('is an error, not a crash, for input that is not JSON, not content, or not alone', () => {
    const notJson = Buffer.from('{', 'utf8')
    const unknown = Buffer.from(JSON.stringify(doc({ type: 'iframe' })), 'utf8')

    expect(() => handle(OP_BUILD, [notJson])).toThrow()
    expect(() => handle(OP_BUILD, [unknown])).toThrow(InvalidContentError)
    expect(() => handle(OP_BUILD, [])).toThrow()
    expect(() => handle(OP_BUILD, [unknown, unknown])).toThrow()
  })
})

/** Documents that between them use every node and mark the builder knows. */
const everything = [
  paragraph(
    text('plain '),
    text('bold', { type: 'bold' }),
    text('italic', { type: 'italic' }),
    text('strike', { type: 'strike' }),
    text('underline', { type: 'underline' }),
    text('code', { type: 'code' }),
    text('link', { type: 'link', attrs: { href: 'https://example.com/a?b=c' } }),
    { type: 'hardBreak' },
    text('both', { type: 'bold' }, { type: 'italic' }),
  ),
  { type: 'heading', attrs: { level: 3 }, content: [text('Heading')] },
  { type: 'blockquote', content: [paragraph(text('quoted'))] },
  {
    type: 'bulletList',
    content: [
      {
        type: 'listItem',
        content: [
          paragraph(text('outer')),
          {
            type: 'orderedList',
            attrs: { start: 3 },
            content: [{ type: 'listItem', content: [paragraph(text('inner'))] }],
          },
        ],
      },
    ],
  },
  {
    type: 'taskList',
    content: [{ type: 'taskItem', attrs: { checked: true }, content: [paragraph(text('done'))] }],
  },
  { type: 'codeBlock', attrs: { language: 'ts' }, content: [text('const a = 1')] },
  { type: 'horizontalRule' },
  {
    type: 'table',
    content: [
      {
        type: 'tableRow',
        content: [
          { type: 'tableHeader', attrs: { colspan: 1 }, content: [paragraph(text('h'))] },
          { type: 'tableCell', attrs: { rowspan: 1 }, content: [paragraph(text('c'))] },
        ],
      },
    ],
  },
  { type: 'image', attrs: { attachmentId: '3f2b8c1e-9d4a-4e6b-8a57-0c1d2e3f4a5b', alt: 'a' } },
  paragraph(
    {
      type: 'attachment',
      attrs: { attachmentId: '3f2b8c1e-9d4a-4e6b-8a57-0c1d2e3f4a5b', fileName: 'a.pdf', size: 3 },
    },
    text('emoji 😀'),
  ),
]

describe('replaceDocument', () => {
  const page = () => [buildDocument(doc(paragraph(text('old one')), paragraph(text('old two'))))]

  it('makes a page read as if it had been built from the new content', () => {
    const old = page()
    const update = replaceDocument(replayed(old), doc(...everything))

    expect(
      replayed([...old, update])
        .getXmlFragment(FIELD)
        .toJSON(),
    ).toBe(fragmentOf(buildDocument(doc(...everything))).toJSON())
  })

  it.each(Object.keys(NODE_ATTRIBUTES))('replaces with a %s', (name) => {
    const old = page()
    const update = replaceDocument(replayed(old), doc({ type: name }))

    expect(plainText(replayed([...old, update]))).not.toContain('old')
  })

  it('holds only what the page lacks: a client that has the page applies it like an edit', () => {
    const old = page()
    const update = replaceDocument(replayed(old), doc(paragraph(text('new'))))

    const client = replayed(old)
    Y.applyUpdate(client, update)

    expect(plainText(client)).toBe('new')
    expect(update.length).toBeLessThan(Y.encodeStateAsUpdate(client).length)
  })

  it('gives the same document when it is applied twice', () => {
    const old = page()
    const update = replaceDocument(replayed(old), doc(paragraph(text('new'))))
    const once = replayed([...old, update])
    const twice = replayed([...old, update, update])

    expect(Y.encodeStateAsUpdate(twice)).toEqual(Y.encodeStateAsUpdate(once))
  })

  it('leaves the first paragraph deleted, also when a client applies it again afterwards', () => {
    const old = page()
    const update = replaceDocument(replayed(old), doc(paragraph(text('new'))))
    const result = replayed([...old, update])
    const before = Y.encodeStateAsUpdate(result)

    Y.applyUpdate(result, firstParagraph())

    expect(Y.encodeStateAsUpdate(result)).toEqual(before)
    expect(result.getXmlFragment(FIELD).length).toBe(1)
    expect(plainText(result)).toBe('new')
  })

  it('also deletes the first paragraph of a page nobody has written to', () => {
    const empty = [buildDocument(doc())]
    const update = replaceDocument(replayed(empty), doc(paragraph(text('new'))))

    const result = replayed([...empty, update])
    expect(result.getXmlFragment(FIELD).length).toBe(1)
    expect(plainText(result)).toBe('new')
  })

  it('ends with an empty paragraph unless the content already does', () => {
    const old = page()
    const update = replaceDocument(replayed(old), doc({ type: 'horizontalRule' }))

    const nodes = (
      replayed([...old, update])
        .getXmlFragment(FIELD)
        .toArray() as Y.XmlElement[]
    ).map((node) => node.nodeName)
    expect(nodes).toEqual(['horizontalRule', 'paragraph'])
  })

  it('leaves one empty paragraph for an empty note, so no client adds its own', () => {
    const old = page()
    const update = replaceDocument(replayed(old), doc())

    const result = replayed([...old, update])
    const nodes = (result.getXmlFragment(FIELD).toArray() as Y.XmlElement[]).map((n) => n.nodeName)
    expect(nodes).toEqual(['paragraph'])
    expect(plainText(result)).toBe('')
  })

  it('deletes text a client typed meanwhile into a block it replaces: the reason the server replaces under the page lock', () => {
    const old = page()
    const update = replaceDocument(replayed(old), doc(paragraph(text('new'))))

    const person = replayed(old)
    const typing: Uint8Array[] = []
    person.on('update', (u: Uint8Array) => typing.push(u))
    const first = person.getXmlFragment(FIELD).get(0) as Y.XmlElement
    ;(first.get(0) as Y.XmlText).insert(3, ' typed')

    expect(plainText(replayed([...old, update, ...typing]))).toBe('new')
  })

  it('keeps a block another client added meanwhile', () => {
    const old = page()
    const update = replaceDocument(replayed(old), doc(paragraph(text('new'))))

    // A person typed a new paragraph at the end while the update was being made.
    const person = replayed(old)
    const typing: Uint8Array[] = []
    person.on('update', (u: Uint8Array) => typing.push(u))
    const added = new Y.XmlElement('paragraph')
    const addedText = new Y.XmlText()
    addedText.insert(0, 'typed meanwhile')
    added.insert(0, [addedText])
    person.getXmlFragment(FIELD).insert(2, [added])

    const one = replayed([...old, update, ...typing])
    const other = replayed([...old, ...typing, update])

    expect(plainText(one)).toContain('new')
    expect(plainText(one)).toContain('typed meanwhile')
    expect(plainText(one)).not.toContain('old')
    expect(Y.encodeStateAsUpdate(one)).toEqual(Y.encodeStateAsUpdate(other))
  })

  it('refuses content the builder refuses, and leaves the page as it was', () => {
    const current = replayed(page())
    const before = Y.encodeStateAsUpdate(current)

    expect(() => replaceDocument(current, doc({ type: 'iframe' }))).toThrow(InvalidContentError)
    expect(() => replaceDocument(current, { type: 'paragraph' })).toThrow(InvalidContentError)
    expect(Y.encodeStateAsUpdate(current)).toEqual(before)
  })
})

describe('the replace operation', () => {
  const json = (content: unknown) => Buffer.from(JSON.stringify(content), 'utf8')

  it('answers with the update for the page it was sent, and the page after it', () => {
    const old = buildDocument(doc(paragraph(text('before'))))

    const [update, stateVector, after, pageText, ...rest] = handle(OP_REPLACE, [
      json(doc(paragraph(text('after')))),
      old,
    ])

    const result = replayed([old, update!])
    expect(plainText(result)).toBe('after')
    expect(stateVector).toEqual(Y.encodeStateVector(result))
    expect(after).toEqual(fingerprintOf(result))
    expect(Buffer.from(pageText!).toString('utf8')).toBe('after')
    expect(rest).toEqual([])
  })

  it('takes a snapshot and the updates after it, as compaction does', () => {
    const old = buildDocument(doc(paragraph(text('before'))))
    const [state] = handle(2, [old])

    const [update] = handle(OP_REPLACE, [json(doc(paragraph(text('after')))), state!])

    expect(plainText(replayed([state!, update!]))).toBe('after')
  })

  it('is an error, not a crash, for input that is not JSON or not content, or without content', () => {
    const old = buildDocument(doc())

    expect(() => handle(OP_REPLACE, [Buffer.from('{'), old])).toThrow()
    expect(() => handle(OP_REPLACE, [json(doc({ type: 'iframe' })), old])).toThrow(
      InvalidContentError,
    )
    expect(() => handle(OP_REPLACE, [])).toThrow()
  })
})
