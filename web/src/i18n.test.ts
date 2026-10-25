import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { applyLocale, detectLocale } from './i18n'

function setLanguages(languages: string[]) {
  vi.stubGlobal('navigator', { languages, language: languages[0] })
}

beforeEach(() => localStorage.clear())
afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('detectLocale', () => {
  it('uses Dutch for a Dutch browser, whatever the region', () => {
    setLanguages(['nl-NL', 'en'])
    expect(detectLocale()).toBe('nl')
    setLanguages(['nl-BE'])
    expect(detectLocale()).toBe('nl')
  })

  it('uses the first language Inkpact has', () => {
    setLanguages(['de-DE', 'nl', 'en'])
    expect(detectLocale()).toBe('nl')
  })

  it('falls back to English', () => {
    setLanguages(['de-DE', 'fr'])
    expect(detectLocale()).toBe('en')
  })

  it('prefers the language the person chose before', () => {
    setLanguages(['nl-NL'])
    applyLocale('en')
    expect(detectLocale()).toBe('en')
  })

  it('ignores a stored value that is not a language', () => {
    setLanguages(['nl'])
    localStorage.setItem('inkpact.locale', 'klingon')
    expect(detectLocale()).toBe('nl')
  })

  it('still works when storage is blocked', () => {
    setLanguages(['nl'])
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    expect(detectLocale()).toBe('nl')
  })
})

describe('applyLocale', () => {
  it('sets the page language and remembers it', () => {
    applyLocale('nl')

    expect(document.documentElement.lang).toBe('nl')
    expect(localStorage.getItem('inkpact.locale')).toBe('nl')
  })

  it('does not fail when storage is blocked', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked')
    })

    expect(() => applyLocale('en')).not.toThrow()
    expect(document.documentElement.lang).toBe('en')
  })
})
