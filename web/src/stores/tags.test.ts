import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeTree, outline } from '../testing/treeFixtures'
import { useTagsStore } from './tags'
import { useTreeStore } from './tree'

beforeEach(() => {
  setActivePinia(createPinia())
  localStorage.clear()
})
afterEach(() => vi.unstubAllGlobals())

describe('tags store', () => {
  it('finds the workspace on its own when nothing has read it yet', async () => {
    fakeTree({ pages: outline(['A']), tags: { A: [{ id: 't1', name: 'Work' }] } })
    const tags = useTagsStore()

    await tags.load()

    expect(tags.items).toEqual([{ id: 't1', name: 'Work', pageCount: 1 }])
    expect(useTreeStore().loaded).toBe(true)
  })

  it('ensureLoaded reads the list once, and load again reads it again', async () => {
    const server = fakeTree({ pages: outline(['A']) })
    const tags = useTagsStore()

    await tags.ensureLoaded()
    await tags.ensureLoaded()
    const reads = server.fetch.mock.calls.length
    await tags.load()

    expect(server.fetch.mock.calls.length).toBeGreaterThan(reads)
  })

  it('pagesOf asks the server for a tag’s pages', async () => {
    fakeTree({
      pages: outline(['A', 'B']),
      tags: { A: [{ id: 't1', name: 'Work' }], B: [{ id: 't1', name: 'Work' }] },
    })
    const tags = useTagsStore()

    const result = await tags.pagesOf('t1')

    expect(result.tagName).toBe('Work')
    expect(result.items.map((i) => i.id)).toEqual(['A', 'B'])
  })

  it('rename changes the name and refreshes the list', async () => {
    const server = fakeTree({ pages: outline(['A']), tags: { A: [{ id: 't1', name: 'Work' }] } })
    const tags = useTagsStore()
    await tags.load()

    const renamed = await tags.rename('t1', 'Job')

    expect(renamed.name).toBe('Job')
    expect(tags.items).toEqual([{ id: 't1', name: 'Job', pageCount: 1 }])
    expect(server.writes()).toEqual([
      { method: 'PATCH', path: '/api/workspaces/w1/tags/t1', body: { name: 'Job' } },
    ])
  })

  it('reset forgets everything', async () => {
    fakeTree({ pages: outline(['A']), tags: { A: [{ id: 't1', name: 'Work' }] } })
    const tags = useTagsStore()
    await tags.load()

    tags.reset()

    expect(tags.items).toEqual([])
    expect(tags.loaded).toBe(false)
  })
})
