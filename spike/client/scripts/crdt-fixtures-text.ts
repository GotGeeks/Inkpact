import type * as Y from 'yjs'
import { getSchema } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { yXmlFragmentToProseMirrorRootNode } from '@tiptap/y-tiptap'

const schema = getSchema([StarterKit])

/** Plain text exactly as ProseMirror sees it: text blocks separated by a newline. */
export function plainText(doc: Y.Doc): string {
  const fragment = doc.getXmlFragment('default')
  // An empty fragment is not a valid TipTap document (the schema wants a block), and a page that
  // is not a TipTap document at all has nothing to extract.
  if (fragment.length === 0) return ''
  const node = yXmlFragmentToProseMirrorRootNode(fragment, schema)
  return node.textBetween(0, node.content.size, '\n')
}
