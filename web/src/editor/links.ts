import { UUID } from './uuid'

/** What a link may point to: web addresses and mail, and pages of this app; nothing a browser would run (`javascript:`, `data:`, `vbscript:`). */
const ALLOWED_PROTOCOLS = new Set(['http:', 'https:', 'mailto:'])

/** A page of this app, as the Joplin import writes a link between notes: exactly `/p/<id>`, no query, no fragment, no dots. */
const PAGE_PATH = new RegExp(`^/p/(${UUID})$`)

/** The id of the page a link points to, when it is a link to a page of this app (`/p/<id>`, or that address in full on this origin). */
export function pageIdOfLink(
  href: string | null | undefined,
  origin: string = window.location.origin,
): string | null {
  if (!href) return null
  const direct = PAGE_PATH.exec(href)
  if (direct) return direct[1]!
  // Copying a link puts its full address on the clipboard; on this origin it is still a page of this app.
  try {
    const url = new URL(href)
    if (url.origin !== origin || url.search || url.hash) return null
    return PAGE_PATH.exec(url.pathname)?.[1] ?? null
  } catch {
    return null
  }
}

export function isAllowedLink(href: string | null | undefined): boolean {
  if (!href) return false
  if (PAGE_PATH.test(href)) return true
  try {
    // No base: a relative address is not a link to anything we know, and "java\tscript:" is parsed like a real one.
    return ALLOWED_PROTOCOLS.has(new URL(href).protocol)
  } catch {
    return false
  }
}

/** The address the editor stores for a link to a page: `/p/<id>`, the one form the index of links and the import know. */
export const pageHref = (pageId: string) => `/p/${pageId}`

/**
 * The address to store for a link the person made: a page of this app, whichever way it was written (a path, or its full address
 * on this origin as the address bar and the clipboard have it), becomes `/p/<id>`. Anything else is left as it is.
 */
export function normalizeLinkHref(href: string, origin: string = window.location.origin): string {
  const pageId = pageIdOfLink(href, origin)
  return pageId ? pageHref(pageId) : href
}
