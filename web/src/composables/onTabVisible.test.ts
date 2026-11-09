import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { onTabVisible, useOnTabVisible } from './onTabVisible'

let state: DocumentVisibilityState = 'visible'
vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => state)

function change(to: DocumentVisibilityState) {
  state = to
  document.dispatchEvent(new Event('visibilitychange'))
}

afterEach(() => (state = 'visible'))

describe('onTabVisible', () => {
  it('calls back when the tab comes into view, not when it goes out of it, until stopped', () => {
    const callback = vi.fn()
    const stop = onTabVisible(callback)

    change('hidden')
    expect(callback).not.toHaveBeenCalled()
    change('visible')
    expect(callback).toHaveBeenCalledOnce()

    stop()
    change('visible')
    expect(callback).toHaveBeenCalledOnce()
  })

  it('lasts as long as the component that uses it', () => {
    const callback = vi.fn()
    const wrapper = mount(
      defineComponent({
        setup() {
          useOnTabVisible(callback)
          return () => h('div')
        },
      }),
    )

    change('visible')
    expect(callback).toHaveBeenCalledOnce()
    wrapper.unmount()
    change('visible')
    expect(callback).toHaveBeenCalledOnce()
  })
})
