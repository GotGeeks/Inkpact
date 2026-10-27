import createDOMPurify from 'dompurify'
import allowList from './allowList.json'
import { isAttachmentId } from './attachmentNodes'
import { isAllowedLink } from './links'

/**
 * What pasted HTML may contain: exactly what the editor's schema can hold, and nothing more. ProseMirror already drops
 * what the schema does not know when it parses; this is the second wall. The lists live in `allowList.json`, because
 * the server's import (run 1.15) sanitises with the same ones, and a test there compares the two.
 *
 * Notes on single entries: `label` and `input` are the editor's task items (a label with a checkbox); `img` is an
 * attachment copied from one page to another, by its id only (see the hook below); `style` is only for the marks
 * (ProseMirror reads it while parsing and drops it); an attachment is its id, its name and size travel with a file, and
 * a description with an image.
 */
const ALLOWED_TAGS: string[] = allowList.tags
const ALLOWED_ATTR: string[] = allowList.attributes

/** Elements whose `style` says something the marks need (Google Docs wraps a whole paste in `<b style="font-weight:normal">`). */
const STYLE_FOR_MARKS = new Set(['B', 'STRONG', 'I', 'EM', 'U', 'S', 'DEL', 'STRIKE', 'SPAN'])

let purifier: ReturnType<typeof createDOMPurify> | null = null

function instance() {
  if (purifier) return purifier
  const purify = createDOMPurify(window)
  purify.addHook('afterSanitizeAttributes', (node) => {
    // A style on anything else would only be one more attribute for a node of the document to carry.
    if (node.hasAttribute('style') && !STYLE_FOR_MARKS.has(node.tagName))
      node.removeAttribute('style')
    if (node.tagName === 'INPUT' && node.getAttribute('type') !== 'checkbox') node.remove()
    // Web addresses and mail only, as for a link made in the editor. (DOMPurify's own address check would also reject
    // attributes like `start` and `colspan`, because it applies the same rule to them.)
    if (node.tagName === 'A' && !isAllowedLink(node.getAttribute('href')))
      node.removeAttribute('href')
    // An image is only ever one of the app's attachments: one from another site is not ours to load (and the policy
    // would block it). There is no `src` on the list; the editor makes the address from the id.
    if (node.tagName === 'IMG' && !isAttachmentId(node.getAttribute('data-attachment-id')))
      node.remove()
    if (
      node.hasAttribute('data-attachment-id') &&
      !isAttachmentId(node.getAttribute('data-attachment-id'))
    )
      node.removeAttribute('data-attachment-id')
  })
  purifier = purify
  return purify
}

// Parsing HTML with something the strict policy blocks in it makes Chromium report a violation, even in a document that is
// never shown: an event handler (`<p onclick=...>`) or a style element (every paste from Word has them). Nothing runs, but
// each report is noise, and the end-to-end test treats any report as a failure. So they are cut out of the text first.
// This is not the wall (DOMPurify is, and what this misses is only reported, never run); it keeps a paste quiet.
const EVENT_HANDLER = /([\s/"'`])on[a-z]+\s*=\s*("[^"]*"|'[^']*'|[^\s>]*)\s*/gi
const ELEMENT_WITH_CODE = /<(style|script)\b[\s\S]*?(?:<\/\1\s*>|$)/gi

export function withoutActiveContent(html: string): string {
  return html
    .replace(ELEMENT_WITH_CODE, '')
    .replace(/<[a-z][^<>]*>/gi, (tag) => tag.replace(EVENT_HANDLER, '$1'))
}

/** Cleans HTML from the clipboard or a drop before the editor parses it. */
export function sanitizePastedHtml(html: string): string {
  return instance().sanitize(withoutActiveContent(html), {
    ALLOWED_TAGS,
    ALLOWED_ATTR,
    ALLOW_DATA_ATTR: false,
    ALLOW_ARIA_ATTR: false,
  })
}
