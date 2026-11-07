import type { EditorView } from '@tiptap/pm/view'

/**
 * The text that is selected in the editor, as the person sees it, or null when nothing is. ProseMirror's own selection follows
 * the browser's one event later (`selectionchange`), so a key pressed right after the arrow keys that made the selection sees
 * the old one. Anything that acts on a key (a shortcut) therefore reads the browser's selection first, and ProseMirror's
 * when the browser has none in the editor.
 */
export function selectedRange(view: EditorView): { from: number; to: number } | null {
  const dom = view.dom.ownerDocument.defaultView?.getSelection()
  if (
    dom &&
    dom.rangeCount > 0 &&
    !dom.isCollapsed &&
    dom.anchorNode &&
    dom.focusNode &&
    view.dom.contains(dom.anchorNode) &&
    view.dom.contains(dom.focusNode)
  ) {
    const anchor = view.posAtDOM(dom.anchorNode, dom.anchorOffset)
    const focus = view.posAtDOM(dom.focusNode, dom.focusOffset)
    const from = Math.min(anchor, focus)
    const to = Math.max(anchor, focus)
    if (from !== to) return { from, to }
  }
  const { from, to } = view.state.selection
  return from === to ? null : { from, to }
}
