import { createI18n } from 'vue-i18n'
import en from './locales/en.json'
import nl from './locales/nl.json'

export const locales = ['en', 'nl'] as const
export type Locale = (typeof locales)[number]

const storageKey = 'inkpact.locale'

function isLocale(value: unknown): value is Locale {
  return (locales as readonly unknown[]).includes(value)
}

/** The language the person chose before, else the browser's language when Inkpact has it, else English. */
export function detectLocale(): Locale {
  try {
    const stored = localStorage.getItem(storageKey)
    if (isLocale(stored)) return stored
  } catch {
    // Storage can be blocked, for example in a private window. The browser language still works.
  }

  const preferred =
    typeof navigator === 'undefined' ? [] : (navigator.languages ?? [navigator.language])
  for (const language of preferred) {
    const base = language.toLowerCase().split('-')[0]
    if (isLocale(base)) return base
  }

  return 'en'
}

/** Keeps the choice for next time, and tells assistive technology and the browser what language the page is in. */
export function applyLocale(locale: Locale) {
  document.documentElement.lang = locale
  try {
    localStorage.setItem(storageKey, locale)
  } catch {
    // Not remembered, which is fine.
  }
}

export function createAppI18n(locale: Locale = 'en') {
  return createI18n({
    legacy: false,
    locale,
    fallbackLocale: 'en',
    messages: { en, nl },
    // Dates are formatted by the browser (Intl) in the language of the page, never written out by hand.
    datetimeFormats: {
      en: { trash: { dateStyle: 'medium', timeStyle: 'short' } },
      nl: { trash: { dateStyle: 'medium', timeStyle: 'short' } },
    },
  })
}
