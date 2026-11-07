import type { Editor } from '@tiptap/core'
import {
  absolutePositionToRelativePosition,
  relativePositionToAbsolutePosition,
  ySyncPluginKey,
} from '@tiptap/y-tiptap'
import type * as Y from 'yjs'

/**
 * A place in the page kept as a Yjs relative position: it stays next to the same text while others edit the page. (A
 * ProseMirror position would not: a change from another client replaces the blocks it touched, or the whole document.)
 * The upload of an attachment and the link picker keep one while they wait.
 */
export interface Place {
  /** Where it is now, or null when the text it was next to was deleted. */
  resolve: () => number | null
}

export function placeAt(editor: Editor, pos: number): Place {
  const binding = ySyncPluginKey.getState(editor.state)?.binding as
    | {
        type: Y.XmlFragment
        doc: Y.Doc
        mapping: Parameters<typeof absolutePositionToRelativePosition>[2]
      }
    | undefined
  if (!binding) return { resolve: () => Math.min(pos, editor.state.doc.content.size) }
  const relative = absolutePositionToRelativePosition(pos, binding.type, binding.mapping)
  return {
    resolve: () =>
      relativePositionToAbsolutePosition(binding.doc, binding.type, relative, binding.mapping),
  }
}
