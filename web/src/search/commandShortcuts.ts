function isModifiedLetter(event: KeyboardEvent, letter: 'k' | 'n'): boolean {
  if (
    event.repeat ||
    event.isComposing ||
    event.keyCode === 229 ||
    event.key === 'Dead' ||
    event.shiftKey ||
    !event.altKey ||
    event.getModifierState('AltGraph')
  ) {
    return false
  }

  const mac = /Mac|iPhone|iPad/.test(globalThis.navigator?.userAgent ?? '')
  if (mac ? !event.metaKey || event.ctrlKey : !event.ctrlKey || event.metaKey) return false

  const key = event.key.toLowerCase()
  return key === letter || (event.code === `Key${letter.toUpperCase()}` && !/^[a-z]$/.test(key))
}

export function isCommandPaletteShortcut(event: KeyboardEvent): boolean {
  return isModifiedLetter(event, 'k')
}

export function isNewPageShortcut(event: KeyboardEvent): boolean {
  return isModifiedLetter(event, 'n')
}
