import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import AppButton from './AppButton.vue'
import AppNotice from './AppNotice.vue'

describe('AppButton', () => {
  it('is a plain button, not a submit, with the outline look by default', () => {
    const wrapper = mount(AppButton, { slots: { default: 'Try again' } })

    expect(wrapper.element.tagName).toBe('BUTTON')
    expect(wrapper.attributes('type')).toBe('button')
    expect(wrapper.attributes('data-variant')).toBe('outline')
    expect(wrapper.text()).toBe('Try again')
  })

  it.each(['primary', 'outline', 'text', 'contained'] as const)(
    'is 44 pixels high on a phone and shows the focus ring as %s',
    (variant) => {
      const classes = mount(AppButton, { props: { variant } }).classes()

      expect(classes).toContain('min-h-11')
      expect(classes).toContain('focus-visible:outline-primary')
    },
  )

  it('passes a click on, and none while disabled', async () => {
    const onClick = vi.fn()
    const wrapper = mount(AppButton, { attrs: { onClick } })
    await wrapper.trigger('click')
    expect(onClick).toHaveBeenCalledTimes(1)

    const disabled = mount(AppButton, { attrs: { onClick, disabled: true } })
    await disabled.trigger('click')
    expect(disabled.attributes('disabled')).toBeDefined()
    expect(onClick).toHaveBeenCalledTimes(1)
  })
})

describe('AppNotice', () => {
  it('is said politely as information', () => {
    const wrapper = mount(AppNotice, { slots: { default: 'Offline mode' } })

    expect(wrapper.attributes('role')).toBe('status')
    expect(wrapper.text()).toBe('Offline mode')
  })

  it('is said at once as an error, and shows its action', () => {
    const wrapper = mount(AppNotice, {
      props: { tone: 'error' },
      slots: { default: 'Could not load', action: '<button>Try again</button>' },
    })

    expect(wrapper.attributes('role')).toBe('alert')
    expect(wrapper.find('button').text()).toBe('Try again')
  })
})
