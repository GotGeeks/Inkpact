import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { Locale } from '../i18n'

export const useSettingsStore = defineStore('settings', () => {
  const locale = ref<Locale>('en')

  function setLocale(next: Locale) {
    locale.value = next
  }

  return { locale, setLocale }
})
