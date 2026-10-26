import { Editor } from '@tiptap/core'
import * as Y from 'yjs'
import { afterEach, describe, expect, it } from 'vitest'
import { createExtensions } from './extensions'
import {
  applyFirstParagraph,
  FIELD,
  FIRST_PARAGRAPH_CLIENT_ID,
  firstParagraphUpdate,
} from './firstParagraph'

const editors: Editor[] = []

function editorFor(doc: Y.Doc): Editor {
  const editor = new Editor({
    element: document.createElement('div'),
    extensions: createExtensions({ doc, placeholder: '', checkboxLabel: () => '' }),
    enableContentCheck: true,
    injectCSS: false,
  })
  editors.push(editor)
  return editor
}

afterEach(() => {
  for (const editor of editors.splice(0)) editor.destroy()
})

/** Two clients that cannot reach each other until `sync` is called: what two people typing at once look like. */
function twoClients(prepare: (doc: Y.Doc) => void) {
  const docs = [new Y.Doc(), new Y.Doc()] as const
  for (const doc of docs) prepare(doc)
  const pending: [Uint8Array[], Uint8Array[]] = [[], []]
  docs.forEach((doc, i) => {
    doc.on('update', (update: Uint8Array, origin: unknown) => {
      if (origin !== 'remote') pending[i]!.push(update)
    })
  })
  const [a, b] = [editorFor(docs[0]), editorFor(docs[1])]
  return {
    docs,
    a,
    b,
    sync() {
      for (const update of pending[0].splice(0)) Y.applyUpdate(docs[1], update, 'remote')
      for (const update of pending[1].splice(0)) Y.applyUpdate(docs[0], update, 'remote')
    },
  }
}

const paragraphs = (editor: Editor) =>
  (editor.getJSON().content ?? []).filter((node) => node.type === 'paragraph')

describe('the first paragraph', () => {
  it('is one fixed update, whoever makes it', () => {
    expect(firstParagraphUpdate()).toEqual(firstParagraphUpdate())
    const other = new Y.Doc()
    applyFirstParagraph(other)
    expect(Y.encodeStateAsUpdate(other)).toEqual(firstParagraphUpdate())
  })

  it('does not change a document that has it already', () => {
    const doc = new Y.Doc()
    applyFirstParagraph(doc)
    const before = Y.encodeStateAsUpdate(doc)
    applyFirstParagraph(doc)
    expect(Y.encodeStateAsUpdate(doc)).toEqual(before)
  })

  it('does not come back after the person deleted it', () => {
    const doc = new Y.Doc()
    applyFirstParagraph(doc)
    doc.getXmlFragment(FIELD).delete(0, 1)
    applyFirstParagraph(doc)
    expect(doc.getXmlFragment(FIELD).length).toBe(0)
  })

  it('is made by a client id no real client has been given', () => {
    const doc = new Y.Doc()
    applyFirstParagraph(doc)
    expect(doc.getXmlFragment(FIELD).length).toBe(1)
    expect([...Y.decodeStateVector(Y.encodeStateVector(doc)).keys()]).toEqual([
      FIRST_PARAGRAPH_CLIENT_ID,
    ])
  })

  it('gives two editors typing at once into a new page one paragraph holding everything', () => {
    const { docs, a, b, sync } = twoClients(applyFirstParagraph)

    a.commands.insertContent('alpha')
    b.commands.insertContent('BRAVO')
    sync()

    for (const editor of [a, b]) {
      expect(paragraphs(editor)).toHaveLength(1)
      const text = editor.getText()
      expect(text).toContain('alpha')
      expect(text).toContain('BRAVO')
    }
    expect(a.getText()).toBe(b.getText())
    // One paragraph with one text in the document itself, not two texts that happen to look like one.
    for (const doc of docs) {
      const fragment = doc.getXmlFragment(FIELD)
      expect(fragment.length).toBe(1)
      expect((fragment.get(0) as Y.XmlElement).length).toBe(1)
    }
  })

  it('is what makes it one: without it the same two editors make two paragraphs', () => {
    const { a, b, sync } = twoClients(() => {})

    a.commands.insertContent('alpha')
    b.commands.insertContent('BRAVO')
    sync()

    expect(paragraphs(a).length).toBeGreaterThan(1)
  })

  it('keeps one paragraph when both clients type many times, with edits in between', () => {
    const { a, b, sync } = twoClients(applyFirstParagraph)

    for (let round = 0; round < 5; round++) {
      a.commands.insertContent(`a${round}`)
      b.commands.insertContent(`b${round}`)
      if (round % 2 === 1) sync()
    }
    sync()

    expect(paragraphs(a)).toHaveLength(1)
    expect(a.getText()).toBe(b.getText())
    for (let round = 0; round < 5; round++) {
      expect(a.getText()).toContain(`a${round}`)
      expect(a.getText()).toContain(`b${round}`)
    }
  })
})
