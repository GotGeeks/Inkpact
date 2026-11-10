import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeTree, outline } from '../testing/treeFixtures'
import { jsonResponse } from '../testing/helpers'
import { useFavoritesStore } from './favorites'
import { useNotificationsStore } from './notifications'
import { useTreeStore } from './tree'

beforeEach(() => {
  setActivePinia(createPinia())
  localStorage.clear()
})
afterEach(() => vi.unstubAllGlobals())

async function started(options: Parameters<typeof fakeTree>[0] = {}) {
  const server = fakeTree({ pages: outline(['A', 'B', 'C']), ...options })
  await useTreeStore().init()
  return { server, favorites: useFavoritesStore(), notices: useNotificationsStore() }
}

describe('favorites store', () => {
  it('reads the list from the server, in its order', async () => {
    const { favorites } = await started({ favorites: ['C', 'A'] })

    await favorites.load()

    expect(favorites.ids).toEqual(['C', 'A'])
    expect(favorites.loaded).toBe(true)
    expect(favorites.loadError).toBeNull()
    expect(favorites.has('A')).toBe(true)
    expect(favorites.has('B')).toBe(false)
  })

  it('exposes a failed read and recovers when the list is retried', async () => {
    const { server, favorites } = await started()
    const original = server.fetch.getMockImplementation()!
    server.fetch.mockImplementation(async (input, init) => {
      if (String(input).endsWith('/favorites')) return jsonResponse(503, { code: 'server_error' })
      return original(input, init)
    })

    await expect(favorites.load()).resolves.toBe(false)
    expect(favorites.loaded).toBe(false)
    expect(favorites.loadError).toBe('server_error')

    server.fetch.mockImplementation(original)
    await expect(favorites.load()).resolves.toBe(true)
    expect(favorites.loaded).toBe(true)
    expect(favorites.loadError).toBeNull()
  })

  it('shows a star at once, before the server has answered', async () => {
    const { favorites, server } = await started()
    let answer!: () => void
    const original = server.fetch.getMockImplementation()!
    server.fetch.mockImplementation(async (input, init) => {
      if (init?.method === 'PUT') await new Promise<void>((resolve) => (answer = resolve))
      return original(input, init)
    })

    const done = favorites.toggle('B')

    expect(favorites.has('B')).toBe(true)
    answer()
    await done
    expect(server.writes()).toEqual([{ method: 'PUT', path: '/api/pages/B/favorite', body: {} }])
  })

  it('removes a star with a DELETE', async () => {
    const { favorites, server } = await started({ favorites: ['A'] })
    await favorites.load()

    await favorites.toggle('A')

    expect(favorites.ids).toEqual([])
    expect(server.writes()).toEqual([{ method: 'DELETE', path: '/api/pages/A/favorite', body: {} }])
  })

  it('puts the list back and posts a notice when the server refuses', async () => {
    const { favorites, server, notices } = await started({ favorites: ['A'] })
    await favorites.load()
    server.failNext(409, 'too_many_favorites')

    await favorites.toggle('B')

    expect(favorites.ids).toEqual(['A'])
    expect(notices.notices).toMatchObject([{ type: 'error', code: 'too_many_favorites' }])
  })

  it('undoes only the refused change, and keeps a star that went through meanwhile', async () => {
    const { favorites, server } = await started()
    const original = server.fetch.getMockImplementation()!
    let refuseA!: () => void
    server.fetch.mockImplementation(async (input, init) => {
      if (init?.method === 'PUT' && String(input).includes('/pages/A/')) {
        await new Promise<void>((resolve) => (refuseA = resolve))
        return new Response(JSON.stringify({ code: 'unknown' }), {
          status: 500,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return original(input, init)
    })

    const starringA = favorites.toggle('A')
    await favorites.toggle('B')
    refuseA()
    await starringA

    expect(favorites.ids).toEqual(['B'])
  })

  it('puts a refused unstar back where it was in the list', async () => {
    const { favorites, server } = await started({ favorites: ['A', 'B', 'C'] })
    await favorites.load()
    server.failNext(500, 'unknown')

    await favorites.toggle('B')

    expect(favorites.ids).toEqual(['A', 'B', 'C'])
  })

  it('reads again when the tab gets back in view, and not after it was reset', async () => {
    const { favorites, server } = await started()
    favorites.watchFocus()
    server.setFavorites(['B'])

    document.dispatchEvent(new Event('visibilitychange'))
    await vi.waitFor(() => expect(favorites.ids).toEqual(['B']))

    favorites.reset()
    server.setFavorites(['C'])
    document.dispatchEvent(new Event('visibilitychange'))
    await Promise.resolve()
    expect(favorites.ids).toEqual([])
  })

  it('does not undo a change with an older answer', async () => {
    const { favorites, server } = await started()
    const original = server.fetch.getMockImplementation()!
    let release!: () => void
    server.fetch.mockImplementation(async (input, init) => {
      const answer = await original(input, init)
      if (String(input).endsWith('/favorites'))
        await new Promise<void>((resolve) => (release = resolve))
      return answer
    })

    const reading = favorites.load()
    await vi.waitFor(() => expect(release).toBeTypeOf('function'))
    await favorites.toggle('A')
    release()
    await reading

    expect(favorites.ids).toEqual(['A'])
  })
})
