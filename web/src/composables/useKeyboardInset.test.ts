import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, nextTick, ref, type Ref } from 'vue'
import { useKeyboardInset } from './useKeyboardInset'

class FakeViewport extends EventTarget {
  height = 800
  offsetTop = 0
  change(height: number, offsetTop = 0) {
    this.height = height
    this.offsetTop = offsetTop
    this.dispatchEvent(new Event('resize'))
  }
}

function setup(active: Ref<boolean>) {
  let inset!: Ref<number>
  const wrapper = mount(
    defineComponent({
      setup() {
        inset = useKeyboardInset(active)
        return () => null
      },
    }),
  )
  return { wrapper, inset: () => inset.value }
}

const property = () => document.documentElement.style.getPropertyValue('--keyboard-inset')

afterEach(() => {
  vi.unstubAllGlobals()
  document.documentElement.style.removeProperty('--keyboard-inset')
})

function stubViewport(layoutHeight = 800) {
  const viewport = new FakeViewport()
  vi.stubGlobal('visualViewport', viewport)
  Object.defineProperty(document.documentElement, 'clientHeight', {
    value: layoutHeight,
    configurable: true,
  })
  return viewport
}

describe('useKeyboardInset', () => {
  it('is zero without a keyboard, and follows it up and down', async () => {
    const viewport = stubViewport()
    const active = ref(true)
    const { inset } = setup(active)
    expect(inset()).toBe(0)
    expect(property()).toBe('0px')

    viewport.change(500)
    expect(inset()).toBe(300)
    expect(property()).toBe('300px')

    viewport.change(800)
    expect(property()).toBe('0px')
  })

  it('counts how far the visual viewport has scrolled, and rounds', () => {
    const viewport = stubViewport()
    const { inset } = setup(ref(true))
    viewport.change(499.6, 100.2)
    // 800 - 499.6 - 100.2
    expect(inset()).toBe(200)
  })

  it('never goes below zero', () => {
    const viewport = stubViewport()
    const { inset } = setup(ref(true))
    viewport.change(900)
    expect(inset()).toBe(0)
  })

  it('writes nothing while inactive, and cleans up when it stops', async () => {
    const viewport = stubViewport()
    const active = ref(false)
    const { inset } = setup(active)
    viewport.change(500)
    expect(property()).toBe('')

    active.value = true
    await nextTick()
    expect(inset()).toBe(300)

    active.value = false
    await nextTick()
    expect(property()).toBe('')
    expect(inset()).toBe(0)
    viewport.change(400)
    expect(property()).toBe('')
  })

  it('removes the property when the component goes away', () => {
    stubViewport()
    const { wrapper } = setup(ref(true))
    expect(property()).toBe('0px')
    wrapper.unmount()
    expect(property()).toBe('')
  })

  it('does nothing in a browser without a visual viewport', () => {
    vi.stubGlobal('visualViewport', undefined)
    const { inset } = setup(ref(true))
    expect(inset()).toBe(0)
    expect(property()).toBe('')
  })
})
