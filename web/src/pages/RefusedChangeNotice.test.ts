import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import RefusedChangeNotice from './RefusedChangeNotice.vue'

let wrapper: VueWrapper | null = null
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
})

function show(reason: 'full' | 'tooLarge', locale: Locale = 'en') {
  wrapper = mount(RefusedChangeNotice, {
    props: { reason },
    global: { plugins: [createAppI18n(locale)] },
    attachTo: document.body,
  })
  return wrapper
}

const button = (name: string) =>
  [...document.body.querySelectorAll('button')].find((item) => item.textContent?.trim() === name)

describe('RefusedChangeNotice', () => {
  it('says why, as an alert, for each reason', () => {
    show('full')
    expect(document.body.querySelector('[role="alert"]')?.textContent).toContain(
      'reached its size limit',
    )
    wrapper!.unmount()

    show('tooLarge')
    expect(document.body.querySelector('[role="alert"]')?.textContent).toContain(
      'too large to save',
    )
    expect(document.body.textContent).not.toContain('Reloading')
  })

  it('asks before it discards, with the focus on the question, and discards on the answer', async () => {
    show('full')

    button('Discard unsaved change')!.click()
    await flushPromises()
    expect(document.activeElement?.textContent).toContain('Discard the change that was not saved?')
    expect(document.body.querySelector('[role="group"]')?.getAttribute('aria-labelledby')).toBe(
      document.activeElement?.id,
    )
    expect(wrapper!.emitted('discard')).toBeUndefined()

    button('Discard')!.click()
    expect(wrapper!.emitted('discard')).toHaveLength(1)
  })

  it('goes back to the button when the person keeps the change', async () => {
    show('full')
    button('Discard unsaved change')!.click()
    await flushPromises()

    button('Keep it for now')!.click()
    await flushPromises()

    expect(document.activeElement?.textContent?.trim()).toBe('Discard unsaved change')
    expect(wrapper!.emitted('discard')).toBeUndefined()
  })

  it('speaks Dutch', () => {
    show('full', 'nl')
    expect(document.body.textContent).toContain('maximale grootte')
    expect(button('Niet-opgeslagen wijziging verwerpen')).toBeDefined()
  })
})
