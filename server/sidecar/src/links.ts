import * as Y from 'yjs'
import { FIELD } from './build.ts'

/**
 * The page a link points to, from its address: exactly `/p/<id>`, which is how the editor and the Joplin import write a link
 * between notes (the editor stores a page address pasted in full, on its own origin, as `/p/<id>` too). A full address is
 * not read: the server does not know which host names are its own (one installation is often reached under two), so it
 * could not tell a page of its own from a page of another installation, and the editor, which does know, would disagree
 * with it. No query, no fragment, no extra path.
 */
const PAGE_PATH = /^\/p\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/

export function pageIdOfHref(href: string): string | null {
  return PAGE_PATH.exec(href)?.[1] ?? null
}

/**
 * The pages a document links to: the distinct ids of its `link` marks that point to a page of the app, sorted. A walk over
 * the Yjs XML tree like `plainText`, so it needs no editor schema; an explicit stack, so a deeply nested page cannot exhaust the
 * call stack. A document without the `default` fragment has no links.
 */
export function pageLinks(doc: Y.Doc): string[] {
  // `getXmlFragment` would make an empty root type out of a name nobody ever wrote to.
  if (!doc.share.has(FIELD)) return []

  const found = new Set<string>()
  const stack: ReturnType<Y.XmlFragment['toArray']>[] = [doc.getXmlFragment(FIELD).toArray()]
  while (stack.length > 0) {
    const children = stack.pop()!
    for (const child of children) {
      if (child instanceof Y.XmlText) {
        for (const op of child.toDelta() as {
          insert: unknown
          attributes?: Record<string, unknown>
        }[]) {
          if (typeof op.insert !== 'string') continue
          const link = op.attributes?.link
          if (typeof link !== 'object' || link === null) continue
          const href = (link as { href?: unknown }).href
          const id = typeof href === 'string' ? pageIdOfHref(href) : null
          if (id) found.add(id)
        }
      } else if (child instanceof Y.XmlElement) {
        stack.push(child.toArray())
      }
    }
  }
  return [...found].sort()
}
