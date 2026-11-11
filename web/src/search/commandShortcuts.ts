/**
 * Ctrl+Alt and a letter, or Cmd+Option on a Mac. Windows reports Ctrl+Alt as AltGraph on a layout that has AltGr, where
 * AltGr and a letter can type a character ("ñ" for N on US-International). In text (a field or the editor) such a key is
 * left to the text. Outside text nothing would be typed, so the shortcut is taken there even when the browser reports
 * AltGraph: otherwise it would never work on those layouts.
 */
function isModifiedLetter(event: KeyboardEvent, letter: 'k' | 'n', inText: boolean): boolean {
  if (
    event.repeat ||
    event.isComposing ||
    event.keyCode === 229 ||
    event.key === 'Dead' ||
    event.shiftKey ||
    !event.altKey ||
    (inText && event.getModifierState('AltGraph'))
  ) {
    return false
  }

  const mac = /Mac|iPhone|iPad/.test(globalThis.navigator?.userAgent ?? '')
  if (mac ? !event.metaKey || event.ctrlKey : !event.ctrlKey || event.metaKey) return false

  const key = event.key.toLowerCase()
  return key === letter || (event.code === `Key${letter.toUpperCase()}` && !/^[a-z]$/.test(key))
}

/** `inText`: the focus is in a field or the editor. Commands open from there too, unless the key types a character. */
export function isCommandPaletteShortcut(event: KeyboardEvent, inText: boolean): boolean {
  return isModifiedLetter(event, 'k', inText)
}

/** `inText` as above. The shell does not make a page from text, but it must not take a key that types a character there. */
export function isNewPageShortcut(event: KeyboardEvent, inText: boolean): boolean {
  return isModifiedLetter(event, 'n', inText)
}
