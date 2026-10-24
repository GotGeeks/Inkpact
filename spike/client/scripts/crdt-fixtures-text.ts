import type * as Y from 'yjs'
import { getSchema } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { yXmlFragmentToProseMirrorRootNode } from '@tiptap/y-tiptap'

const schema = getSchema([StarterKit])

/** Plain text exactly as ProseMirror sees it: text blocks separated by a newline. */
export function plainText(doc: Y.Doc): string {
  const node = yXmlFragmentToProseMirrorRootNode(doc.getXmlFragment('default'), schema)
  return node.textBetween(0, node.content.size, '\n')
}
