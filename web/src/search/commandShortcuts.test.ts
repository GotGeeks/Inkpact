import { afterEach, describe, expect, it, vi } from 'vitest'
import { isCommandPaletteShortcut, isNewPageShortcut } from './commandShortcuts'

afterEach(() => vi.unstubAllGlobals())

function press(
  key: string,
  init: KeyboardEventInit = {},
  userAgent = 'Windows',
  code = `Key${key.toUpperCase()}`,
): KeyboardEvent {
  vi.stubGlobal('navigator', { userAgent })
  return new KeyboardEvent('keydown', { key, code, ...init })
}

describe('command palette shortcuts', () => {
  it('uses Ctrl+Alt+K / Ctrl+Alt+N on Windows and Linux', () => {
    expect(isCommandPaletteShortcut(press('k', { ctrlKey: true, altKey: true }))).toBe(true)
    expect(isNewPageShortcut(press('n', { ctrlKey: true, altKey: true }))).toBe(true)
  })

  it('uses Cmd+Option+K / Cmd+Option+N on macOS', () => {
    const mac = 'Macintosh'
    expect(isCommandPaletteShortcut(press('k', { metaKey: true, altKey: true }, mac))).toBe(true)
    expect(isNewPageShortcut(press('n', { metaKey: true, altKey: true }, mac))).toBe(true)
  })

  it('requires exactly the listed modifiers', () => {
    expect(isCommandPaletteShortcut(press('k', { ctrlKey: true }))).toBe(false)
    expect(
      isCommandPaletteShortcut(press('k', { ctrlKey: true, altKey: true, shiftKey: true })),
    ).toBe(false)
    expect(
      isCommandPaletteShortcut(press('k', { ctrlKey: true, metaKey: true, altKey: true })),
    ).toBe(false)
  })

  it('does not take repeated, composing, dead-key or AltGraph input', () => {
    expect(isNewPageShortcut(press('n', { ctrlKey: true, altKey: true, repeat: true }))).toBe(false)
    expect(isNewPageShortcut(press('n', { ctrlKey: true, altKey: true, isComposing: true }))).toBe(
      false,
    )
    expect(
      isNewPageShortcut(press('Dead', { ctrlKey: true, altKey: true }, 'Windows', 'KeyN')),
    ).toBe(false)
    expect(
      isNewPageShortcut(
        press('n', { ctrlKey: true, altKey: true, keyCode: 229 }, 'Windows', 'KeyN'),
      ),
    ).toBe(false)
    const altGraph = press('n', { ctrlKey: true, altKey: true })
    vi.spyOn(altGraph, 'getModifierState').mockReturnValue(true)
    expect(isNewPageShortcut(altGraph)).toBe(false)
  })

  it('recognizes the key position on non-Latin layouts but not another Latin letter', () => {
    expect(
      isCommandPaletteShortcut(press('к', { ctrlKey: true, altKey: true }, 'Windows', 'KeyK')),
    ).toBe(true)
    expect(
      isCommandPaletteShortcut(press('j', { ctrlKey: true, altKey: true }, 'Windows', 'KeyK')),
    ).toBe(false)
  })
})
