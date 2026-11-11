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
    expect(isCommandPaletteShortcut(press('k', { ctrlKey: true, altKey: true }), false)).toBe(true)
    expect(isNewPageShortcut(press('n', { ctrlKey: true, altKey: true }), false)).toBe(true)
  })

  it('uses Cmd+Option+K / Cmd+Option+N on macOS', () => {
    const mac = 'Macintosh'
    expect(isCommandPaletteShortcut(press('k', { metaKey: true, altKey: true }, mac), false)).toBe(
      true,
    )
    expect(isNewPageShortcut(press('n', { metaKey: true, altKey: true }, mac), false)).toBe(true)
  })

  it('requires exactly the listed modifiers', () => {
    expect(isCommandPaletteShortcut(press('k', { ctrlKey: true }), false)).toBe(false)
    expect(
      isCommandPaletteShortcut(press('k', { ctrlKey: true, altKey: true, shiftKey: true }), false),
    ).toBe(false)
    expect(
      isCommandPaletteShortcut(press('k', { ctrlKey: true, metaKey: true, altKey: true }), false),
    ).toBe(false)
  })

  it('does not take repeated, composing, dead-key or AltGraph input', () => {
    expect(
      isNewPageShortcut(press('n', { ctrlKey: true, altKey: true, repeat: true }), false),
    ).toBe(false)
    expect(
      isNewPageShortcut(press('n', { ctrlKey: true, altKey: true, isComposing: true }), false),
    ).toBe(false)
    expect(
      isNewPageShortcut(press('Dead', { ctrlKey: true, altKey: true }, 'Windows', 'KeyN'), false),
    ).toBe(false)
    expect(
      isNewPageShortcut(
        press('n', { ctrlKey: true, altKey: true, keyCode: 229 }, 'Windows', 'KeyN'),
        false,
      ),
    ).toBe(false)
  })

  it('leaves an AltGraph key to text, and takes it outside text, where it types nothing', () => {
    // Ctrl+Alt+N on US-International: Windows reports AltGraph, and in text the key types "ñ".
    const altGraph = (key: string, letter: string) => {
      const event = press(key, { ctrlKey: true, altKey: true }, 'Windows', `Key${letter}`)
      vi.spyOn(event, 'getModifierState').mockImplementation((name) => name === 'AltGraph')
      return event
    }
    expect(isNewPageShortcut(altGraph('ñ', 'N'), true)).toBe(false)
    expect(isCommandPaletteShortcut(altGraph('k', 'K'), true)).toBe(false)
    expect(isNewPageShortcut(altGraph('ñ', 'N'), false)).toBe(true)
    expect(isCommandPaletteShortcut(altGraph('k', 'K'), false)).toBe(true)
  })

  it('opens commands from text when the key types nothing there', () => {
    expect(isCommandPaletteShortcut(press('k', { ctrlKey: true, altKey: true }), true)).toBe(true)
  })

  it('recognizes the key position on non-Latin layouts but not another Latin letter', () => {
    expect(
      isCommandPaletteShortcut(
        press('к', { ctrlKey: true, altKey: true }, 'Windows', 'KeyK'),
        false,
      ),
    ).toBe(true)
    expect(
      isCommandPaletteShortcut(
        press('j', { ctrlKey: true, altKey: true }, 'Windows', 'KeyK'),
        false,
      ),
    ).toBe(false)
  })
})
