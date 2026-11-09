import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeTree, outline } from '../testing/treeFixtures'
import { useRecentStore } from './recent'
import { useTreeStore } from './tree'

beforeEach(() => {
  setActivePinia(createPinia())
  localStorage.clear()
})
afterEach(() => vi.unstubAllGlobals())

const recent = [
  { pageId: 'B', openedAt: '2026-11-09T11:00:00Z' },
  { pageId: 'A', openedAt: '2026-11-09T10:00:00Z' },
]

describe('recent store', () => {
  it('reads the list the server has, newest first', async () => {
    fakeTree({ pages: outline(['A', 'B']), recent })
    await useTreeStore().init()
    const store = useRecentStore()

    await store.load()

    expect(store.items).toEqual(recent)
  })

  it('tells the server a page was opened', async () => {
    const server = fakeTree({ pages: outline(['A', 'B']) })
    const store = useRecentStore()

    await store.visit('A')

    expect(server.writes()).toEqual([{ method: 'POST', path: '/api/pages/A/visit', body: {} }])
  })

  it('puts a page that is opened at the front at once', async () => {
    fakeTree({ pages: outline(['A', 'B']), recent })
    await useTreeStore().init()
    const store = useRecentStore()
    await store.load()

    void store.visit('A')

    expect(store.items.map((item) => item.pageId)).toEqual(['A', 'B'])
  })

  it('waits for a visit still being sent before it reads the list', async () => {
    const server = fakeTree({ pages: outline(['A', 'B']), recent })
    await useTreeStore().init()
    const original = server.fetch.getMockImplementation()!
    let send!: () => void
    server.fetch.mockImplementation(async (input, init) => {
      if (init?.method === 'POST') await new Promise<void>((resolve) => (send = resolve))
      return original(input, init)
    })
    const store = useRecentStore()

    const visiting = store.visit('A')
    const reading = store.load()
    await vi.waitFor(() => expect(send).toBeTypeOf('function'))
    // Not read yet: the server does not have the visit.
    expect(server.fetch.mock.calls.some((call) => String(call[0]).endsWith('/recent'))).toBe(false)
    send()
    await Promise.all([visiting, reading])

    expect(store.items[0]!.pageId).toBe('A')
  })

  it('ignores a failed visit', async () => {
    const server = fakeTree({ pages: outline(['A']) })
    server.failNext(500, 'unknown')

    await expect(useRecentStore().visit('A')).resolves.toBeUndefined()
  })

  it('reads again when the tab gets back in view', async () => {
    const server = fakeTree({ pages: outline(['A', 'B']) })
    await useTreeStore().init()
    const store = useRecentStore()
    store.watchFocus()
    server.setRecent(recent)

    document.dispatchEvent(new Event('visibilitychange'))

    await vi.waitFor(() => expect(store.items).toEqual(recent))
    store.unwatchFocus()
  })
})
