import { getMarkRange } from '@tiptap/core'
import type { EditorState } from '@tiptap/pm/state'

export interface TextRange {
  from: number
  to: number
}

/**
 * True when the text at `from`–`to` is code: a code block, or inline code. A link cannot go there (inline code excludes every
 * other mark), so nothing that makes one is offered.
 */
export function inCode(state: EditorState, from: number, to: number = from): boolean {
  if (state.doc.resolve(from).parent.type.spec.code) return true
  const code = state.schema.marks.code
  if (!code) return false
  if (from < to) return state.doc.rangeHasMark(from, to, code)
  return code.isInSet(state.storedMarks ?? state.doc.resolve(from).marks()) !== undefined
}

/**
 * The text a link command acts on: the whole link when the caret, or all of the selected text, is in one link (to change or
 * remove that link, never a part of it, which would split it in two); otherwise the selected text. Null when there is nothing
 * to link: no selection outside a link, or code.
 *
 * `selected` is the selection as the person sees it (`selectedRange`), null when nothing is selected. With nothing selected the
 * caret counts as in a link as TipTap's `isActive('link')` has it: right after a link that was just made it is not, as the link
 * mark was switched off there (see `insertPageLink`), although `getMarkRange` would still find that link.
 */
export function linkableRange(state: EditorState, selected: TextRange | null): TextRange | null {
  const link = state.schema.marks.link
  if (!link) return null
  if (selected && selected.from < selected.to) {
    if (inCode(state, selected.from, selected.to)) return null
    const around = getMarkRange(state.doc.resolve(selected.from), link)
    return around && around.from <= selected.from && selected.to <= around.to ? around : selected
  }
  const $caret = state.selection.$from
  if (!link.isInSet(state.storedMarks ?? $caret.marks())) return null
  const around = getMarkRange($caret, link)
  return around && around.from < around.to ? around : null
}
