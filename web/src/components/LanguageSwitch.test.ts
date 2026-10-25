import { mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { describe, expect, it } from 'vitest'
import { createAppI18n } from '../i18n'
import { useSettingsStore } from '../stores/settings'
import LanguageSwitch from './LanguageSwitch.vue'

function setup() {
  const pinia = createPinia()
  const i18n = createAppI18n('en')
  const wrapper = mount(LanguageSwitch, { global: { plugins: [pinia, i18n] } })
  return { wrapper, i18n, settings: useSettingsStore(pinia) }
}

describe('LanguageSwitch', () => {
  it('has an accessible group label in the current language', () => {
    const { wrapper } = setup()
    expect(wrapper.find('[role="group"]').attributes('aria-label')).toBe('Language')
  })

  it('switches to Dutch and updates the store', async () => {
    const { wrapper, i18n, settings } = setup()
    const nl = wrapper.findAll('button').find((b) => b.text() === 'Nederlands')
    await nl!.trigger('click')
    expect(i18n.global.locale.value).toBe('nl')
    expect(settings.locale).toBe('nl')
    expect(wrapper.find('[role="group"]').attributes('aria-label')).toBe('Taal')
  })

  it('keeps the language when the active button is pressed again', async () => {
    const { wrapper, i18n } = setup()
    const en = wrapper.findAll('button').find((b) => b.text() === 'English')
    await en!.trigger('click')
    expect(i18n.global.locale.value).toBe('en')
  })
})
