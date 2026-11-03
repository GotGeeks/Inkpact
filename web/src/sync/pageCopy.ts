import * as Y from 'yjs'
import { applyFirstParagraph, FIELD } from '../editor/firstParagraph'

/**
 * The first update of a new page whose content is that of `before` (a page as one update), written the way every page is:
 * from the fixed first paragraph, which is deleted, with the blocks after it. A client that opens the page and applies
 * the first paragraph again changes nothing.
 */
export function copyUpdate(before: Uint8Array): Uint8Array {
  const source = new Y.Doc()
  const copy = new Y.Doc()
  try {
    Y.applyUpdate(source, before)
    const blocks = source
      .getXmlFragment(FIELD)
      .toArray()
      .filter((node): node is Y.XmlElement | Y.XmlText => !(node instanceof Y.XmlHook))
      .map((node) => node.clone())
    applyFirstParagraph(copy)
    const fragment = copy.getXmlFragment(FIELD)
    copy.transact(() => {
      fragment.delete(0, fragment.length)
      fragment.insert(0, blocks)
    })
    return Y.encodeStateAsUpdate(copy)
  } finally {
    source.destroy()
    copy.destroy()
  }
}

/** Bytes as base64, for a JSON body. In pieces, so a large page does not overflow the argument list. */
export function toBase64(bytes: Uint8Array): string {
  let binary = ''
  for (let at = 0; at < bytes.length; at += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(at, at + 0x8000))
  }
  return btoa(binary)
}

/**
 * A new page id (a version 4 UUID). `crypto.randomUUID` is only there in a secure context, and the app is also served over
 * plain http on a home network; `getRandomValues` is there either way.
 */
export function newPageId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6]! & 0x0f) | 0x40
  bytes[8] = (bytes[8]! & 0x3f) | 0x80
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
