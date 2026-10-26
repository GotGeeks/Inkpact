import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { RouterView } from 'vue-router'
import { createAppI18n } from '../i18n'
import SyncStatus from '../sync/SyncStatus.vue'
import { errorFrame, FakeSyncHub } from '../testing/fakeSyncHub'
import { jsonResponse, mountAt, signedOut, state, stubApi } from '../testing/helpers'
import { fakeTree, outline } from '../testing/treeFixtures'
import { useNotificationsStore } from './notifications'
import { useSyncStore } from './sync'

const page = '0a1b2c3d-0000-4000-8000-000000000001'
const otherPage = '0a1b2c3d-0000-4000-8000-000000000002'

let hub: FakeSyncHub
let wrapper: VueWrapper | null = null

beforeEach(() => {
  hub = new FakeSyncHub()
  vi.stubGlobal('WebSocket', function () {
    return hub.createSocket()
  })
  setActivePinia(createPinia())
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  vi.useRealTimers()
})

describe('the sync store', () => {
  it('makes the client when the first page opens, and shows live once the page is in step', async () => {
    const sync = useSyncStore()
    expect(sync.display).toBe('connecting')

    sync.open(page)
    await flushPromises()

    expect(sync.display).toBe('live')
    expect(sync.pages[page]).toEqual({ state: 'synced', readOnly: false, unsent: false })
    sync.destroy()
  })

  it('shows offline when the connection is lost, and live again after it is back', async () => {
    const sync = useSyncStore()
    sync.open(page)
    await flushPromises()

    hub.current.serverClose(1006)
    expect(sync.display).toBe('offline')
    expect(sync.pages[page]!.state).toBe('syncing')

    await vi.waitFor(() => expect(sync.display).toBe('live'), { timeout: 2000 })
    sync.destroy()
  })

  it('opens one socket for any number of pages, and forgets a page that is closed', async () => {
    const sync = useSyncStore()
    sync.open(page)
    sync.open(otherPage)
    await flushPromises()
    expect(hub.attempts).toBe(1)

    sync.close(page)
    expect(sync.pages[page]).toBeUndefined()
    expect(Object.keys(sync.pages)).toHaveLength(1)
    sync.destroy()
  })

  it('ends the socket on destroy, and starts a new one for the next page', async () => {
    const sync = useSyncStore()
    sync.open(page)
    await flushPromises()
    sync.destroy()
    expect(sync.pages).toEqual({})
    expect(hub.sockets).toHaveLength(0)

    sync.open(page)
    await flushPromises()
    expect(hub.attempts).toBe(2)
    sync.destroy()
  })

  it('marks a missing page', async () => {
    hub.missing.add(page)
    const sync = useSyncStore()
    sync.open(page)
    await flushPromises()
    expect(sync.pages[page]!.state).toBe('missing')
    sync.destroy()
  })

  it('tells the person about an error that belongs to the connection', async () => {
    const sync = useSyncStore()
    sync.open(page)
    await flushPromises()

    hub.current.deliver(errorFrame(page, 'too_many_pages'))

    expect(useNotificationsStore().notices).toMatchObject([
      { type: 'error', code: 'too_many_pages' },
    ])
    sync.destroy()
  })

  it('flags a session the server ended, after asking who is signed in', async () => {
    const fetchMock = stubApi([state(signedOut)])
    const sync = useSyncStore()
    sync.open(page)
    await flushPromises()

    hub.current.serverClose(4401, 'session_ended')

    await vi.waitFor(() => expect(sync.sessionEnded).toBe(true))
    expect(fetchMock).toHaveBeenCalledWith('/api/auth/state', expect.anything())
    sync.destroy()
  })

  it('does not take an unreachable server for a signed-out person', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('offline')))
    vi.useFakeTimers()
    const sync = useSyncStore()
    sync.open(page)
    await vi.advanceTimersByTimeAsync(0)
    hub.refusing = true
    hub.current.serverClose(1006)

    await vi.advanceTimersByTimeAsync(5000)

    expect(sync.sessionEnded).toBe(false)
    sync.destroy()
  })

  it('asks before the tab closes only while an editable page has changes the server lacks', async () => {
    const sync = useSyncStore()
    const handle = sync.open(page)
    await flushPromises()
    const before = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(before)
    expect(before.defaultPrevented).toBe(false)

    hub.current.serverClose(1006)
    handle.doc.getText('t').insert(0, 'x')
    const during = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(during)
    expect(during.defaultPrevented).toBe(true)

    sync.destroy()
  })
})

describe('the sync pill', () => {
  it('shows each state in words, as a status for screen readers', async () => {
    const sync = useSyncStore()
    wrapper = mount(SyncStatus, { global: { plugins: [createAppI18n('en')] } })
    const pill = wrapper.get('[role="status"]')
    expect(pill.text()).toBe('Connecting…')

    sync.open(page)
    await flushPromises()
    expect(pill.text()).toBe('Live')
    expect(pill.attributes('data-state')).toBe('live')

    hub.current.serverClose(1006)
    await flushPromises()
    expect(pill.text()).toContain('Offline')
    expect(pill.attributes('data-state')).toBe('offline')
    sync.destroy()
  })

  it('speaks Dutch', () => {
    wrapper = mount(SyncStatus, { global: { plugins: [createAppI18n('nl')] } })
    expect(wrapper.text()).toBe('Verbinden…')
  })
})

describe('the page and the shell', () => {
  it('opens the page over the socket and shows the pill; a new page gets its first paragraph once', async () => {
    fakeTree({ pages: outline(['Notes']) })
    const mounted = await mountAt(RouterView, '/p/Notes')
    wrapper = mounted.wrapper

    await vi.waitFor(() => expect(wrapper!.get('[data-testid="sync-status"]').text()).toBe('Live'))
    expect(hub.sent(0, 'Notes')).toHaveLength(1)
    expect(hub.sent(2, 'Notes')).toHaveLength(1)
  })

  it('goes to the login page, and back to this page after signing in, when the session ends', async () => {
    const server = fakeTree({ pages: outline(['Notes']) })
    const original = server.fetch.getMockImplementation()!
    let ended = false
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) =>
      ended && String(input) === '/api/auth/state'
        ? jsonResponse(200, signedOut)
        : original(input, init),
    )
    const mounted = await mountAt(RouterView, '/p/Notes')
    wrapper = mounted.wrapper
    await vi.waitFor(() => expect(wrapper!.get('[data-testid="sync-status"]').text()).toBe('Live'))

    ended = true
    hub.current.serverClose(4401, 'session_ended')

    await vi.waitFor(() => expect(mounted.router.currentRoute.value.name).toBe('login'))
    expect(mounted.router.currentRoute.value.query.redirect).toBe('/p/Notes')
  })

  it('shows the not-found message when the server has no such page', async () => {
    hub.missing.add('Ghost')
    // The tree knows the page, so only the sync socket says it is gone.
    fakeTree({ pages: outline(['Ghost']) })
    const mounted = await mountAt(RouterView, '/p/Ghost')
    wrapper = mounted.wrapper

    await vi.waitFor(() => expect(wrapper!.get('main').text()).toContain('could not be found'))
  })
})
