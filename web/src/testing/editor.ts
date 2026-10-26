import { Editor } from '@tiptap/core'
import * as Y from 'yjs'
import { createExtensions, type EditorExtensionsOptions } from '../editor/extensions'
import { applyFirstParagraph } from '../editor/firstParagraph'

const made: Editor[] = []

/** A real editor on a fresh document that holds its first paragraph, as a page does once it is opened. */
export function makeEditor(options: Partial<EditorExtensionsOptions> = {}): Editor {
  const doc = new Y.Doc()
  applyFirstParagraph(doc)
  const editor = new Editor({
    element: document.body.appendChild(document.createElement('div')),
    extensions: createExtensions({ doc, placeholder: '', checkboxLabel: () => '', ...options }),
    injectCSS: false,
  })
  made.push(editor)
  return editor
}

export function destroyEditors() {
  for (const editor of made.splice(0)) editor.destroy()
  document.body.innerHTML = ''
}

/** Types the way a keyboard does: each character goes through ProseMirror's text input handlers, so input rules run. */
export function typeText(editor: Editor, text: string) {
  const { view } = editor
  for (const char of text) {
    const { from, to } = view.state.selection
    const handled = view.someProp('handleTextInput', (handler) =>
      handler(view, from, to, char, () => view.state.tr),
    )
    if (!handled) view.dispatch(view.state.tr.insertText(char, from, to))
  }
}

/** Presses a key; true when something in the editor took it. */
export function pressKey(editor: Editor, key: string): boolean {
  const { view } = editor
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })
  return view.someProp('handleKeyDown', (handler) => handler(view, event)) === true
}
