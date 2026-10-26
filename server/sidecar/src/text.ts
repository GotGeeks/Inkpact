import * as Y from 'yjs'

/** The field TipTap's collaboration extension keeps the document in. */
const FIELD = 'default'

type Children = ReturnType<Y.XmlFragment['toArray']>

/**
 * The text of a page for search: a walk over the Yjs XML tree, so it needs no editor schema and keeps
 * working when the editor gains nodes. Not exactly ProseMirror's `textBetween`, which search does not
 * need. A document without the `default` fragment has no text.
 */
export function plainText(doc: Y.Doc): string {
  // `getXmlFragment` would make an empty root type out of a name nobody ever wrote to.
  if (!doc.share.has(FIELD)) return ''

  let text = ''
  let atLineStart = true
  const append = (piece: string) => {
    if (piece.length === 0) return
    text += piece
    atLineStart = piece.endsWith('\n')
  }
  const startLine = () => {
    if (!atLineStart) append('\n')
  }

  // An explicit stack instead of recursion: a deeply nested page must not be able to exhaust the call stack.
  const stack: { children: Children; next: number; block: boolean }[] = [
    { children: doc.getXmlFragment(FIELD).toArray(), next: 0, block: false },
  ]
  while (stack.length > 0) {
    const frame = stack[stack.length - 1]!
    if (frame.next === frame.children.length) {
      stack.pop()
      if (frame.block) startLine()
      continue
    }

    const child = frame.children[frame.next++]
    if (child instanceof Y.XmlText) {
      for (const op of child.toDelta() as { insert: unknown }[]) {
        // Embeds are objects or types, and attributes (link targets, image sources) are not in the inserts.
        if (typeof op.insert === 'string') append(op.insert)
      }
    } else if (child instanceof Y.XmlElement) {
      const grandchildren = child.toArray()
      if (grandchildren.length === 0) {
        // A hard break or an image: nothing to say, but the line ends.
        append('\n')
      } else {
        startLine()
        stack.push({ children: grandchildren, next: 0, block: true })
      }
    }
  }

  return text
    .split('\n')
    .map((line) => line.trimEnd())
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim()
}
