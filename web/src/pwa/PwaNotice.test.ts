import { mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { reactive } from 'vue'
import { createAppI18n } from '../i18n'
import { useSyncStore } from '../stores/sync'
import PwaNotice from './PwaNotice.vue'

const fake = vi.hoisted(() => ({
  state: null as unknown as {
    updateReady: boolean
    registrationFailed: boolean
    otherTabsOpen: boolean
  },
  applyUpdate: vi.fn(() => true),
}))
vi.mock('./registration', () => ({
  get pwaState() {
    return fake.state
  },
  applyUpdate: fake.applyUpdate,
}))

let wrapper: VueWrapper | null = null
let secure = true

beforeEach(() => {
  localStorage.clear()
  setActivePinia(createPinia())
  fake.state = reactive({ updateReady: false, registrationFailed: false, otherTabsOpen: false })
  fake.applyUpdate.mockClear()
  secure = true
  Object.defineProperty(window, 'isSecureContext', { configurable: true, get: () => secure })
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.restoreAllMocks()
})

function notice() {
  wrapper = mount(PwaNotice, { global: { plugins: [createAppI18n('en')] } })
  return wrapper
}

const button = (name: string) => wrapper!.findAll('button').find((b) => b.text() === name)

describe('the app notice', () => {
  it('says once per browser that installing needs HTTPS, not on every page load', async () => {
    secure = false
    notice()
    expect(wrapper!.text()).toContain('require HTTPS')
    await button('Dismiss')!.trigger('click')
    expect(wrapper!.text()).not.toContain('require HTTPS')

    wrapper!.unmount()
    notice()
    expect(wrapper!.text()).not.toContain('require HTTPS')
  })

  it('offers to update now when nothing waits to be sent, and puts it off with Later', async () => {
    fake.state.updateReady = true
    notice()
    expect(wrapper!.get('[data-testid="pwa-update"]').text()).toContain('An update is ready')
    await button('Update now')!.trigger('click')
    expect(fake.applyUpdate).toHaveBeenCalledOnce()

    await button('Later')!.trigger('click')
    expect(wrapper!.find('[data-testid="pwa-update"]').exists()).toBe(false)
  })

  it('does not offer to update while changes have not reached the server', async () => {
    fake.state.updateReady = true
    useSyncStore().lostEdits = [
      { id: 'l1', pageId: 'p1', before: new Uint8Array(), durable: true, ready: true },
    ]
    notice()
    expect(wrapper!.text()).toContain('Wait until your changes reach the server')
    expect(button('Update now')).toBeUndefined()
  })

  it('asks to close the other tabs when the update was refused for them', async () => {
    fake.state.updateReady = true
    fake.state.otherTabsOpen = true
    notice()
    expect(wrapper!.text()).toContain('Close the other tabs, then update')
  })
})
