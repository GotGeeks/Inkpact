import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { useOnline } from './useOnline'

function setOnline(value: boolean) {
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(value)
  window.dispatchEvent(new Event(value ? 'online' : 'offline'))
}

function mountWith(onChange?: (online: boolean) => void) {
  return mount(
    defineComponent({
      setup() {
        const online = useOnline(onChange)
        return () => h('p', online.value ? 'online' : 'offline')
      },
    }),
  )
}

afterEach(() => vi.restoreAllMocks())

describe('useOnline', () => {
  it('starts with what the browser says and follows its events', async () => {
    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(true)
    const wrapper = mountWith()
    expect(wrapper.text()).toBe('online')

    setOnline(false)
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toBe('offline')

    setOnline(true)
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toBe('online')
  })

  it('tells the caller about each change, and stops when the component is gone', () => {
    const onChange = vi.fn()
    const wrapper = mountWith(onChange)

    setOnline(false)
    expect(onChange).toHaveBeenLastCalledWith(false)

    wrapper.unmount()
    setOnline(true)
    expect(onChange).toHaveBeenCalledTimes(1)
  })
})
