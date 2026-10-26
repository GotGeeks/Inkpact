/** What a link may point to: web addresses and mail, nothing a browser would run (`javascript:`, `data:`, `vbscript:`). */
const ALLOWED_PROTOCOLS = new Set(['http:', 'https:', 'mailto:'])

export function isAllowedLink(href: string | null | undefined): boolean {
  if (!href) return false
  try {
    // No base: a relative address is not a link to anything we know, and "java\tscript:" is parsed like a real one.
    return ALLOWED_PROTOCOLS.has(new URL(href).protocol)
  } catch {
    return false
  }
}
