import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope, h, nextTick, ref, type Component } from 'vue'
import { useNotificationsStore } from '../stores/notifications'
import { useHistoryPanel, type HistoryPanelComponent } from './useHistoryPanel'

const pageA = '0a1b2c3d-0000-4000-8000-00000000000a'
const pageB = '0a1b2c3d-0000-4000-8000-00000000000b'
const Panel = { render: () => h('div', 'panel') } as unknown as HistoryPanelComponent

let stop: () => void = () => {}

function start(load: () => Promise<HistoryPanelComponent>) {
  const pageId = ref(pageA)
  const scope = effectScope()
  const panel = scope.run(() => useHistoryPanel(pageId, load))!
  stop = () => scope.stop()
  return { pageId, panel }
}

beforeEach(() => setActivePinia(createPinia()))
afterEach(() => {
  stop()
  vi.restoreAllMocks()
})

describe('useHistoryPanel', () => {
  it('fetches the panel once, when it is first asked for, and opens it once it is there', async () => {
    let arrive: (panel: HistoryPanelComponent) => void = () => {}
    const load = vi.fn(() => new Promise<HistoryPanelComponent>((resolve) => (arrive = resolve)))
    const { panel } = start(load)
    expect(load).not.toHaveBeenCalled()

    const showing = panel.show()
    expect(panel.loading.value).toBe(true)
    expect(panel.open.value).toBe(false)
    // Asked again while it is on its way: nothing more.
    void panel.show()
    expect(load).toHaveBeenCalledOnce()
    arrive(Panel)
    await showing

    expect(panel.loading.value).toBe(false)
    expect(panel.open.value).toBe(true)
    expect(panel.component.value).toBe(Panel)
    panel.hide()
    await panel.show()
    expect(load).toHaveBeenCalledOnce()
    expect(panel.open.value).toBe(true)
  })

  it('says so when the panel cannot be fetched, stays closed, and can be asked for again', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    let fail = true
    const load = vi.fn(() =>
      fail
        ? Promise.reject(new TypeError('Failed to fetch dynamically imported module'))
        : Promise.resolve(Panel),
    )
    const { panel } = start(load)

    await panel.show()

    expect(panel.open.value).toBe(false)
    expect(panel.loading.value).toBe(false)
    expect(useNotificationsStore().notices).toEqual([
      expect.objectContaining({ type: 'error', code: 'history_open_failed' }),
    ])

    fail = false
    await panel.show()
    expect(panel.open.value).toBe(true)
  })

  it('does not open over the next page when the person left the page while it was fetched', async () => {
    let arrive: (panel: HistoryPanelComponent) => void = () => {}
    const { pageId, panel } = start(() => new Promise((resolve) => (arrive = resolve)))

    const showing = panel.show()
    pageId.value = pageB
    await nextTick()
    arrive(Panel)
    await showing

    expect(panel.open.value).toBe(false)
  })

  it('closes when another page opens', async () => {
    const { pageId, panel } = start(() => Promise.resolve(Panel))
    await panel.show()

    pageId.value = pageB
    await nextTick()

    expect(panel.open.value).toBe(false)
  })

  it('is closed when the panel goes away with the page, so it does not come back by itself', async () => {
    // As the page view has it: the panel inside the page, cleared when it is unmounted for any reason.
    const { panel } = start(() => Promise.resolve(Panel))
    await panel.show()
    const pageShown = ref(true)
    const view = mount({
      setup: () => () =>
        pageShown.value && panel.open.value && panel.component.value
          ? h(panel.component.value as Component, { onVnodeUnmounted: panel.hide })
          : h('p', 'not found'),
    })
    expect(view.text()).toBe('panel')

    // The page is gone (not found on the socket, say), and comes back.
    pageShown.value = false
    await nextTick()
    pageShown.value = true
    await nextTick()

    expect(panel.open.value).toBe(false)
    expect(view.text()).toBe('not found')
    view.unmount()
  })
})
