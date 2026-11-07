import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it } from 'vitest'
import { destroyEditors, makeEditor } from '../testing/editor'
import { inCode, linkableRange } from './linkRange'

afterEach(destroyEditors)

/** Where `text` is in the document (its first occurrence inside one text node). */
function find(editor: Editor, text: string): { from: number; to: number } {
  let found: { from: number; to: number } | null = null
  editor.state.doc.descendants((node, pos) => {
    if (found || !node.isText) return
    const at = node.text!.indexOf(text)
    if (at >= 0) found = { from: pos + at, to: pos + at + text.length }
  })
  if (!found) throw new Error(`"${text}" is not in the document`)
  return found
}

function page() {
  const editor = makeEditor()
  editor.commands.insertContent(
    '<p>hello <a href="/p/8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f40">the world</a> and <code>inline</code> end</p><pre><code>block code</code></pre>',
  )
  return editor
}

describe('the text a link command acts on', () => {
  it('is the selected text when it is not in a link', () => {
    const editor = page()
    const hello = find(editor, 'hello')

    expect(linkableRange(editor.state, hello)).toEqual(hello)
  })

  it('is the whole link when only a part of it is selected, so a change or a removal does not split it', () => {
    const editor = page()
    const link = find(editor, 'the world')
    const part = find(editor, 'wor')

    expect(linkableRange(editor.state, part)).toEqual(link)
    expect(linkableRange(editor.state, link)).toEqual(link)
  })

  it('is the selected text when it goes beyond the link', () => {
    const editor = page()
    const link = find(editor, 'the world')
    const wider = { from: link.from - 3, to: link.to }

    expect(linkableRange(editor.state, wider)).toEqual(wider)
  })

  it('is the whole link with the caret in it, and nothing with the caret outside one', () => {
    const editor = page()
    const link = find(editor, 'the world')

    editor.commands.setTextSelection(link.from + 2)
    expect(linkableRange(editor.state, null)).toEqual(link)

    editor.commands.setTextSelection(find(editor, 'hello').from + 1)
    expect(linkableRange(editor.state, null)).toBeNull()
  })

  it('is nothing with the caret right after a link that was just made: the link mark was switched off there', () => {
    const editor = page()
    const link = find(editor, 'the world')
    editor.chain().setTextSelection(link.to).unsetMark('link').run()

    expect(linkableRange(editor.state, null)).toBeNull()
  })

  it('is nothing in code, where a link cannot go: a code block, or inline code', () => {
    const editor = page()

    expect(linkableRange(editor.state, find(editor, 'block'))).toBeNull()
    expect(linkableRange(editor.state, find(editor, 'inline'))).toBeNull()
    // Partly code: the link would not go on that part.
    const and = find(editor, ' and ')
    expect(
      linkableRange(editor.state, { from: and.from, to: find(editor, 'inline').to }),
    ).toBeNull()
  })
})

describe('code', () => {
  it('is a code block or inline code, for a range and for the caret', () => {
    const editor = page()
    const inline = find(editor, 'inline')

    expect(inCode(editor.state, find(editor, 'block').from + 1)).toBe(true)
    expect(inCode(editor.state, inline.from, inline.to)).toBe(true)
    expect(inCode(editor.state, inline.from + 2)).toBe(true)
    const hello = find(editor, 'hello')
    expect(inCode(editor.state, hello.from, hello.to)).toBe(false)
    expect(inCode(editor.state, hello.from + 2)).toBe(false)
  })
})
