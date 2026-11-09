import { Editor } from '@tiptap/core'
import * as Y from 'yjs'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createExtensions } from './extensions'
import { applyFirstParagraph, FIELD } from './firstParagraph'

const editors: Editor[] = []

afterEach(() => {
  for (const editor of editors.splice(0)) editor.destroy()
})

/** A page with a block that a newer version of the app made, which this schema does not have. */
function pageFromTheFuture(): Y.Doc {
  const doc = new Y.Doc()
  applyFirstParagraph(doc)
  doc.getXmlFragment(FIELD).insert(1, [new Y.XmlElement('futureBlock')])
  return doc
}

function editorFor(doc: Y.Doc, onInvalidContent?: (error: Error) => void): Editor {
  const editor = new Editor({
    element: document.createElement('div'),
    extensions: createExtensions({
      doc,
      placeholder: '',
      checkboxLabel: () => '',
      onInvalidContent,
    }),
    injectCSS: false,
  })
  editors.push(editor)
  return editor
}

const nodeNames = (doc: Y.Doc) =>
  doc
    .getXmlFragment(FIELD)
    .toArray()
    .map((node) => (node as Y.XmlElement).nodeName)

describe('a page with a block this schema does not know', () => {
  it('keeps the block, and writes nothing to the page, even when nobody is told', () => {
    const doc = pageFromTheFuture()
    const writes = vi.fn()
    doc.on('update', writes)

    const editor = editorFor(doc)
    editor.commands.insertContent('typed anyway')

    expect(nodeNames(doc)).toEqual(['paragraph', 'futureBlock'])
    expect(writes).not.toHaveBeenCalled()
  })

  it('says so', () => {
    const onInvalidContent = vi.fn()

    editorFor(pageFromTheFuture(), onInvalidContent)

    expect(onInvalidContent).toHaveBeenCalledOnce()
    expect(onInvalidContent.mock.calls[0]![0]).toBeInstanceOf(Error)
  })
})
