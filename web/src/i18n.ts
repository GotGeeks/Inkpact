import { createI18n } from 'vue-i18n'
import en from './locales/en.json'
import nl from './locales/nl.json'

export const locales = ['en', 'nl'] as const
export type Locale = (typeof locales)[number]

export function createAppI18n(locale: Locale = 'en') {
  return createI18n({
    legacy: false,
    locale,
    fallbackLocale: 'en',
    messages: { en, nl },
  })
}
