import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { applyTheme, detectTheme } from './theme'

beforeEach(() => {
  localStorage.clear()
  document.documentElement.removeAttribute('data-theme')
})
afterEach(() => vi.restoreAllMocks())

describe('detectTheme', () => {
  it('is System when nothing was chosen', () => {
    expect(detectTheme()).toBe('system')
  })

  it('gives back the theme that was chosen', () => {
    applyTheme('dark')
    expect(detectTheme()).toBe('dark')
    applyTheme('light')
    expect(detectTheme()).toBe('light')
  })

  it('ignores a stored value that is not a theme', () => {
    localStorage.setItem('inkpact.theme', 'neon')
    expect(detectTheme()).toBe('system')
  })

  it('survives blocked storage', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('blocked', 'SecurityError')
    })
    expect(detectTheme()).toBe('system')
  })
})

describe('applyTheme', () => {
  it('sets data-theme for Light and Dark and removes it for System', () => {
    applyTheme('light')
    expect(document.documentElement.getAttribute('data-theme')).toBe('light')
    applyTheme('dark')
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
    applyTheme('system')
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false)
    expect(localStorage.getItem('inkpact.theme')).toBeNull()
  })

  it('makes the browser bar follow a chosen theme, and the system again for System', () => {
    document.head.innerHTML = `
      <meta name="theme-color" data-scheme="light" content="#f7f7fa" media="(prefers-color-scheme: light)">
      <meta name="theme-color" data-scheme="dark" content="#11131a" media="(prefers-color-scheme: dark)">`
    const media = () =>
      [...document.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]')].map(
        (m) => m.media,
      )

    applyTheme('light')
    expect(media()).toEqual(['all', 'not all'])
    applyTheme('dark')
    expect(media()).toEqual(['not all', 'all'])
    applyTheme('system')
    expect(media()).toEqual(['(prefers-color-scheme: light)', '(prefers-color-scheme: dark)'])
    document.head.innerHTML = ''
  })

  it('still sets the theme when storage is blocked', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('blocked', 'SecurityError')
    })
    expect(() => applyTheme('dark')).not.toThrow()
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
  })
})
