import { Editor, type JSONContent } from '@tiptap/core'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { buildDocument, MARK_ATTRIBUTES, NODE_ATTRIBUTES, replaceDocument } from 'sidecar/src/build'
import * as Y from 'yjs'
import { afterEach, describe, expect, it } from 'vitest'
import { createExtensions } from './extensions'
import { applyFirstParagraph, firstParagraphUpdate } from './firstParagraph'

// What the Joplin import (run 1.15) writes is made by the sidecar's builder from JSON, and read by the editor. These tests
// are the contract between the two: if the builder or the editor changes, they must still agree.

const editors: Editor[] = []

function editorFor(doc: Y.Doc, errors: unknown[] = []): Editor {
  const editor = new Editor({
    element: document.createElement('div'),
    extensions: createExtensions({ doc, placeholder: '', checkboxLabel: () => '' }),
    enableContentCheck: true,
    injectCSS: false,
    onContentError: ({ error }) => errors.push(error),
  })
  editors.push(editor)
  return editor
}

afterEach(() => {
  for (const editor of editors.splice(0)) editor.destroy()
})

const ID = '3f2b8c1e-9d4a-4e6b-8a57-0c1d2e3f4a5b'
const text = (value: string, ...marks: NonNullable<JSONContent['marks']>): JSONContent => ({
  type: 'text',
  text: value,
  ...(marks.length > 0 ? { marks } : {}),
})
const paragraph = (...content: JSONContent[]): JSONContent => ({ type: 'paragraph', content })
const doc = (...content: JSONContent[]): JSONContent => ({ type: 'doc', content })
const cell = (type: 'tableCell' | 'tableHeader', value: string, attrs = {}): JSONContent => ({
  type,
  attrs,
  content: [paragraph(text(value))],
})

/** Documents that between them use every node and every mark the builder knows. */
const fixtures: [string, JSONContent][] = [
  ['paragraphs', doc(paragraph(text('One')), paragraph(text('Two')))],
  [
    'every mark',
    doc(
      paragraph(
        text('bold', { type: 'bold' }),
        text(' '),
        text('italic', { type: 'italic' }),
        text(' '),
        text('strike', { type: 'strike' }),
        text(' '),
        text('underline', { type: 'underline' }),
        text(' '),
        text('code', { type: 'code' }),
        text(' '),
        text('link', { type: 'link', attrs: { href: 'https://example.com/a?b=c' } }),
        text(' '),
        text('both', { type: 'bold' }, { type: 'italic' }),
      ),
    ),
  ],
  [
    'headings 1 to 6',
    doc(
      ...[1, 2, 3, 4, 5, 6].map((level) => ({
        type: 'heading',
        attrs: { level },
        content: [text(`Heading ${level}`)],
      })),
    ),
  ],
  [
    'a quote',
    doc({ type: 'blockquote', content: [paragraph(text('quoted')), paragraph(text('twice'))] }),
  ],
  [
    'a bullet list with a nested list',
    doc({
      type: 'bulletList',
      content: [
        {
          type: 'listItem',
          content: [
            paragraph(text('outer')),
            {
              type: 'bulletList',
              content: [{ type: 'listItem', content: [paragraph(text('inner'))] }],
            },
          ],
        },
        { type: 'listItem', content: [paragraph(text('second'))] },
      ],
    }),
  ],
  [
    'a numbered list that starts at 3',
    doc({
      type: 'orderedList',
      attrs: { start: 3 },
      content: [{ type: 'listItem', content: [paragraph(text('three'))] }],
    }),
  ],
  [
    'a task list',
    doc({
      type: 'taskList',
      content: [
        { type: 'taskItem', attrs: { checked: false }, content: [paragraph(text('open'))] },
        { type: 'taskItem', attrs: { checked: true }, content: [paragraph(text('done'))] },
      ],
    }),
  ],
  [
    'code blocks, with and without a language',
    doc(
      { type: 'codeBlock', attrs: { language: 'ts' }, content: [text('const a = 1\nconst b = 2')] },
      { type: 'codeBlock', content: [text('plain')] },
      { type: 'codeBlock' },
    ),
  ],
  ['a rule', doc(paragraph(text('above')), { type: 'horizontalRule' }, paragraph(text('below')))],
  ['a hard break', doc(paragraph(text('one'), { type: 'hardBreak' }, text('two')))],
  [
    'a table',
    doc({
      type: 'table',
      content: [
        {
          type: 'tableRow',
          content: [cell('tableHeader', 'Name'), cell('tableHeader', 'Value')],
        },
        {
          type: 'tableRow',
          content: [cell('tableCell', 'a'), cell('tableCell', 'wide', { colspan: 1, rowspan: 2 })],
        },
        { type: 'tableRow', content: [cell('tableCell', 'b')] },
      ],
    }),
  ],
  ['an image', doc({ type: 'image', attrs: { attachmentId: ID, alt: 'a picture' } })],
  [
    'a file in the text',
    doc(
      paragraph(
        text('see '),
        { type: 'attachment', attrs: { attachmentId: ID, fileName: 'manual.pdf', size: 1234 } },
        text(' for more'),
      ),
    ),
  ],
  ['text with characters outside the basic plane', doc(paragraph(text('emoji 😀 and 字')))],
]

/**
 * Builds `content` with the sidecar, loads it into the real editor, and checks that the editor gives back the same
 * document: the input as ProseMirror reads it (attributes left out are filled with the schema's defaults, as `getJSON` does),
 * and, when it does not end in a paragraph, with the empty one the editor adds (the builder writes it, so no client has to).
 */
function expectRoundTrip(content: JSONContent) {
  const built = new Y.Doc()
  Y.applyUpdate(built, buildDocument(content))
  const before = Y.encodeStateAsUpdate(built)
  const errors: unknown[] = []
  const editor = editorFor(built, errors)

  const blocks = content.content ?? []
  const trailing = blocks.at(-1)?.type === 'paragraph' ? [] : [{ type: 'paragraph' }]
  const expected = editor.schema
    .nodeFromJSON({ ...content, content: [...blocks, ...trailing] })
    .toJSON()

  expect(errors).toEqual([])
  expect(editor.getJSON()).toEqual(expected)
  // Opening the page changes nothing in the document: no repair of a table, no paragraph at the end. Every client that
  // opened it would otherwise write its own.
  expect(Y.encodeStateAsUpdate(built)).toEqual(before)
}

describe('what the sidecar builds, the editor reads back', () => {
  it.each(fixtures)('%s', (_name, content) => expectRoundTrip(content))

  it('also when it arrives in a second client that applied the first paragraph before it', () => {
    const client = new Y.Doc()
    applyFirstParagraph(client)
    Y.applyUpdate(client, buildDocument(doc(paragraph(text('hello')))))

    expect(editorFor(client).getJSON().content).toEqual([
      { type: 'paragraph', content: [{ type: 'text', text: 'hello' }] },
    ])
  })
})

// The documents the server's conversion is expected to make (server/tests/Inkpact.Api.Tests/Fixtures/import): the same file
// the C# tests check the converter against, so what the converter makes is what the editor can open.
const conversions = JSON.parse(
  readFileSync(
    // The tests run from web/.
    resolve(process.cwd(), '../server/tests/Inkpact.Api.Tests/Fixtures/import/conversions.json'),
    'utf8',
  ),
) as {
  markdown: { name: string; expected: JSONContent[] }[]
  html: { name: string; expected: JSONContent[] }[]
}

describe.each([
  ['markdown', conversions.markdown],
  ['html', conversions.html],
] as const)('what the converter makes from %s, the editor opens', (_kind, cases) => {
  it.each(cases.map((c) => [c.name, c.expected] as const))('%s', (_name, blocks) =>
    expectRoundTrip(doc(...blocks)),
  )
})

describe('the first paragraph', () => {
  it('is the same update the clients make, byte for byte', () => {
    expect(buildDocument(doc())).toEqual(firstParagraphUpdate())
  })

  it('does not come back when a client applies it after the content', () => {
    const client = new Y.Doc()
    Y.applyUpdate(client, buildDocument(doc(paragraph(text('hello')))))
    const before = Y.encodeStateAsUpdate(client)

    applyFirstParagraph(client)

    expect(Y.encodeStateAsUpdate(client)).toEqual(before)
    expect(editorFor(client).getJSON().content).toHaveLength(1)
  })
})

/**
 * A page that was imported, a person has open (their client applied the first paragraph and the import's update), and the
 * server replaces its content with an update (run 1.16a). The editor must read the result as the content, and opening it
 * must write nothing: `expectRoundTrip` for a page that is replaced instead of built.
 */
function expectReplacement(content: JSONContent) {
  const old = buildDocument(doc(paragraph(text('what was there')), paragraph(text('and more'))))
  const server = new Y.Doc()
  Y.applyUpdate(server, old)
  const update = replaceDocument(server, content)

  const client = new Y.Doc()
  applyFirstParagraph(client)
  Y.applyUpdate(client, old)
  Y.applyUpdate(client, update)
  const before = Y.encodeStateAsUpdate(client)
  const errors: unknown[] = []
  const editor = editorFor(client, errors)

  const blocks = content.content ?? []
  const trailing =
    blocks.length === 0 || blocks.at(-1)?.type !== 'paragraph' ? [{ type: 'paragraph' }] : []
  const expected = editor.schema
    .nodeFromJSON({ ...content, content: [...blocks, ...trailing] })
    .toJSON()

  expect(errors).toEqual([])
  expect(editor.getJSON()).toEqual(expected)
  expect(Y.encodeStateAsUpdate(client)).toEqual(before)

  // Neither a second delivery nor the first paragraph arriving again changes the page.
  Y.applyUpdate(client, update)
  applyFirstParagraph(client)
  expect(Y.encodeStateAsUpdate(client)).toEqual(before)
}

describe('what the server replaces a page with, the editor reads back', () => {
  it.each(fixtures)('%s', (_name, content) => expectReplacement(content))

  it('an empty note leaves one empty paragraph', () => expectReplacement(doc()))

  it.each([
    ...conversions.markdown.map((c) => [`markdown: ${c.name}`, c.expected] as const),
    ...conversions.html.map((c) => [`html: ${c.name}`, c.expected] as const),
  ])('what the converter makes from %s', (_name, blocks) => expectReplacement(doc(...blocks)))
})

describe('the builder and the editor agree on what exists', () => {
  const schema = editorFor(new Y.Doc()).schema

  it('has every node of the schema, and only those', () => {
    const nodes = Object.keys(schema.nodes).filter((name) => name !== 'doc' && name !== 'text')

    expect(Object.keys(NODE_ATTRIBUTES).sort()).toEqual(nodes.sort())
  })

  it('has every mark of the schema, and only those', () => {
    expect(Object.keys(MARK_ATTRIBUTES).sort()).toEqual(Object.keys(schema.marks).sort())
  })

  it.each(Object.entries(NODE_ATTRIBUTES))('allows the attributes of %s', (name, attributes) => {
    expect([...attributes].sort()).toEqual(Object.keys(schema.nodes[name]!.spec.attrs ?? {}).sort())
  })

  it.each(Object.entries(MARK_ATTRIBUTES))(
    'allows the attributes of the mark %s',
    (name, attributes) => {
      expect([...attributes].sort()).toEqual(
        Object.keys(schema.marks[name]!.spec.attrs ?? {}).sort(),
      )
    },
  )
})
