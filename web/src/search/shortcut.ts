/**
 * Whether a key press is Ctrl+K, or Cmd+K on a Mac, the search shortcut: without Alt or Shift. K is the letter on a
 * Latin layout (also Dvorak, where it is elsewhere), and the key in the place of K where the layout has no Latin
 * letters (Cyrillic, Greek).
 */
export function isSearchShortcut(event: KeyboardEvent): boolean {
  if (event.altKey || event.shiftKey || !(event.ctrlKey || event.metaKey)) return false
  const key = event.key.toLowerCase()
  return key === 'k' || (event.code === 'KeyK' && !/^[a-z]$/.test(key))
}
