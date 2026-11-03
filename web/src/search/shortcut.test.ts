import { describe, expect, it } from 'vitest'
import { isSearchShortcut } from './shortcut'

const press = (init: KeyboardEventInit) => isSearchShortcut(new KeyboardEvent('keydown', init))

describe('isSearchShortcut', () => {
  it('is Ctrl+K and Cmd+K, in either case', () => {
    expect(press({ key: 'k', code: 'KeyK', ctrlKey: true })).toBe(true)
    expect(press({ key: 'k', code: 'KeyK', metaKey: true })).toBe(true)
    expect(press({ key: 'K', code: 'KeyK', ctrlKey: true })).toBe(true)
  })

  it('is not K alone, nor with Alt or Shift, nor another letter', () => {
    expect(press({ key: 'k', code: 'KeyK' })).toBe(false)
    expect(press({ key: 'k', code: 'KeyK', ctrlKey: true, altKey: true })).toBe(false)
    expect(press({ key: 'K', code: 'KeyK', ctrlKey: true, shiftKey: true })).toBe(false)
    expect(press({ key: 'j', code: 'KeyJ', ctrlKey: true })).toBe(false)
  })

  it('is the key in the place of K on a layout without Latin letters', () => {
    expect(press({ key: 'л', code: 'KeyK', ctrlKey: true })).toBe(true)
    expect(press({ key: 'κ', code: 'KeyK', ctrlKey: true })).toBe(true)
  })

  it('is the letter K on a Latin layout that has it elsewhere, and not the letter in its place', () => {
    // Dvorak: the K is where QWERTY has V, and the place of K types a T.
    expect(press({ key: 'k', code: 'KeyV', ctrlKey: true })).toBe(true)
    expect(press({ key: 't', code: 'KeyK', ctrlKey: true })).toBe(false)
  })
})
