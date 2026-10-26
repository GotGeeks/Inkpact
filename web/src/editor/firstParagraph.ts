import * as Y from 'yjs'

/**
 * The client id of the one document that makes a page's first paragraph. It is part of the stored format: never change
 * it. Every page's document starts with the same update from this id, so Yjs treats all copies as the same item and a
 * second one is a no-op.
 */
export const FIRST_PARAGRAPH_CLIENT_ID = 1

/** The field of the document that TipTap's collaboration extension and the server's text extract both read. */
export const FIELD = 'default'

let cached: Uint8Array | null = null

/**
 * One empty paragraph, as a Yjs update. Two clients typing into a brand-new empty page would each create their own first
 * paragraph (run 0.3), and the page would end up with two. If both start from this update instead, they type into the
 * same paragraph. The paragraph holds an empty text node on purpose: without it each client would create its own text
 * inside the one paragraph, which is the same problem one level down.
 */
export function firstParagraphUpdate(): Uint8Array {
  if (cached) return cached
  const doc = new Y.Doc()
  doc.clientID = FIRST_PARAGRAPH_CLIENT_ID
  const paragraph = new Y.XmlElement('paragraph')
  paragraph.insert(0, [new Y.XmlText()])
  doc.getXmlFragment(FIELD).insert(0, [paragraph])
  cached = Y.encodeStateAsUpdate(doc)
  doc.destroy()
  return cached
}

/**
 * Gives a new document its first paragraph. A page that has had content for a long time has it already, and applying it
 * again changes nothing; a page where the person deleted it does not get it back, because the deletion is in the
 * document's delete set.
 */
export function applyFirstParagraph(doc: Y.Doc) {
  Y.applyUpdate(doc, firstParagraphUpdate())
}
