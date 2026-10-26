import createDOMPurify from 'dompurify'
import { isAllowedLink } from './links'

/**
 * What pasted HTML may contain: exactly what the editor's schema can hold, and nothing more. ProseMirror already drops
 * what the schema does not know when it parses; this is the second wall, and the list the server-side check of the
 * import (run 1.15) has to match.
 */
const ALLOWED_TAGS = [
  'p',
  'h1',
  'h2',
  'h3',
  'h4',
  'h5',
  'h6',
  'ul',
  'ol',
  'li',
  'blockquote',
  'pre',
  'code',
  'strong',
  'b',
  'em',
  'i',
  'u',
  's',
  'del',
  'strike',
  'a',
  'br',
  'hr',
  'table',
  'thead',
  'tbody',
  'tr',
  'th',
  'td',
  'span',
  'div',
  // Task items: the editor writes a label with a checkbox.
  'label',
  'input',
]

const ALLOWED_ATTR = [
  'href',
  'colspan',
  'rowspan',
  'start',
  'data-type',
  'data-checked',
  'type',
  'checked',
  // Only for the marks: ProseMirror reads it while parsing and drops it. See the hook below.
  'style',
]

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
