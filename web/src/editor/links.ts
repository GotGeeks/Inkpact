/** What a link may point to: web addresses and mail, and pages of this app; nothing a browser would run (`javascript:`, `data:`, `vbscript:`). */
const ALLOWED_PROTOCOLS = new Set(['http:', 'https:', 'mailto:'])

/** A page of this app, as the Joplin import writes a link between notes: exactly `/p/<id>`, no query, no fragment, no dots. */
const PAGE_PATH = /^\/p\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/

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
