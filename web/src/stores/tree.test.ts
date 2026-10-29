import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeTree, outline, workspace } from '../testing/treeFixtures'
import { useNotificationsStore } from './notifications'
import { useTreeStore } from './tree'

const pages = outline(['A', '  A1', '    A1a', 'B'])

beforeEach(() => {
  setActivePinia(createPinia())
  localStorage.clear()
})
afterEach(() => vi.unstubAllGlobals())

async function started(options: Parameters<typeof fakeTree>[0] = { pages }) {
  const server = fakeTree(options)
  const tree = useTreeStore()
  await tree.init()
  return { server, tree, notices: useNotificationsStore() }
}

describe('tree store: reading', () => {
  it('finds the workspace and reads its tree and its trash', async () => {
    const { tree } = await started({ pages, trash: outline(['Gone']) })

    expect(tree.workspace).toMatchObject({ id: workspace.id, name: 'Private' })
    expect(tree.pages.map((p) => p.id)).toEqual(['A', 'A1', 'A1a', 'B'])
    expect(tree.trash.map((p) => p.id)).toEqual(['Gone'])
    expect(tree.treeVersion).toBe(1)
    expect(tree.loaded).toBe(true)
  })

  it('does the work once, however often it is asked', async () => {
    const { server, tree } = await started()
    const calls = server.fetch.mock.calls.length

    await tree.init()

    expect(server.fetch.mock.calls.length).toBe(calls)
  })

  it('copes with a person who has no workspace', async () => {
    const server = fakeTree()
    server.fetch.mockImplementationOnce(
      async () =>
        new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }),
    )
    const tree = useTreeStore()

    await tree.init()

    expect(tree.workspace).toBeNull()
    expect(tree.pages).toEqual([])
  })

  it.each([
    ['owner', true],
    ['editor', true],
    ['viewer', false],
  ] as const)('a %s can edit: %s', async (role, canEdit) => {
    const { tree } = await started({ pages, role })

    expect(tree.canEdit).toBe(canEdit)
  })

  it('looks pages up by id', async () => {
    const { tree } = await started()

    expect(tree.byId.get('A1')?.title).toBe('A1')
    expect(tree.byId.get('ghost')).toBeUndefined()
  })

  it('reads one page, and says nothing is there for a page that is not found', async () => {
    const { tree } = await started({ pages, trash: outline(['Gone']) })

    expect((await tree.fetchPage('A'))?.inTrash).toBe(false)
    expect((await tree.fetchPage('Gone'))?.inTrash).toBe(true)
    expect(await tree.fetchPage('ghost')).toBeNull()
  })

  it('forgets everything on reset', async () => {
    const { tree } = await started()

    tree.reset()

    expect(tree.pages).toEqual([])
    expect(tree.workspace).toBeNull()
    expect(tree.loaded).toBe(false)
    expect(tree.expanded.size).toBe(0)
  })
})

describe('tree store: which branches are open', () => {
  it('remembers them for the workspace, and starts from what was remembered', async () => {
    const first = await started()
    first.tree.setExpanded('A', true)
    first.tree.setExpanded('A1', true)
    first.tree.setExpanded('A1', false)
    setActivePinia(createPinia())

    const second = await started()

    expect([...second.tree.expanded]).toEqual(['A'])
  })

  it('opens every branch above a page', async () => {
    const { tree } = await started()

    tree.reveal('A1a')

    expect([...tree.expanded].sort()).toEqual(['A', 'A1'])
  })

  it('leaves what is open alone when the page is already on screen', async () => {
    const { tree } = await started()
    tree.setExpanded('A', true)
    const before = tree.expanded

    tree.reveal('A1')

    expect(tree.expanded).toBe(before)
  })

  it('starts closed when what was remembered is not usable', async () => {
    localStorage.setItem(`inkpact.tree.expanded.${workspace.id}`, '{"not":"a list"}')
    const { tree } = await started()
    expect(tree.expanded.size).toBe(0)

    setActivePinia(createPinia())
    localStorage.setItem(`inkpact.tree.expanded.${workspace.id}`, 'not json at all')
    expect((await started()).tree.expanded.size).toBe(0)
  })

  it('still works when storage is blocked', async () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    const { tree } = await started()

    expect(() => tree.setExpanded('A', true)).not.toThrow()
    expect(tree.expanded.has('A')).toBe(true)
    vi.restoreAllMocks()
  })
})

describe('tree store: changing', () => {
  it('creates a page, naming the neighbours and never a key, and opens the parent', async () => {
    const { server, tree } = await started()

    const change = await tree.create('New', { parentId: 'A', afterId: 'A1' })

    expect(change?.page.title).toBe('New')
    expect(server.writes()).toEqual([
      {
        method: 'POST',
        path: '/api/workspaces/w1/pages',
        body: { title: 'New', parentId: 'A', afterId: 'A1', beforeId: null },
      },
    ])
    expect(tree.expanded.has('A')).toBe(true)
  })

  it('returns a page it made also when reading the tree afterwards fails, so the caller does not make it again', async () => {
    const { server, tree } = await started()
    const answer = server.fetch.getMockImplementation()!
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if ((init?.method ?? 'GET') === 'GET') throw new TypeError('Failed to fetch')
      return answer(input, init)
    })

    const change = await tree.create('New')

    expect(change?.page.title).toBe('New')
    expect(server.writes()).toHaveLength(1)
  })

  it('creates at the top level, last, when nothing is said', async () => {
    const { server, tree } = await started()

    await tree.create('Top')

    expect(server.writes()[0]!.body).toEqual({
      title: 'Top',
      parentId: null,
      afterId: null,
      beforeId: null,
    })
    expect(tree.expanded.size).toBe(0)
  })

  it('renames with the version it last saw', async () => {
    const { server, tree } = await started()

    await tree.rename('A1', 'Renamed')

    expect(server.writes()).toEqual([
      { method: 'PATCH', path: '/api/pages/A1', body: { title: 'Renamed', expectedVersion: 1 } },
    ])
  })

  it('moves with a position and the version, and opens the new parent', async () => {
    const { server, tree } = await started()

    await tree.move('B', { parentId: 'A1', beforeId: 'A1a' })

    expect(server.writes()).toEqual([
      {
        method: 'POST',
        path: '/api/pages/B/move',
        body: { parentId: 'A1', afterId: null, beforeId: 'A1a', expectedVersion: 1 },
      },
    ])
    expect(tree.expanded.has('A1')).toBe(true)
  })

  it('reads the tree again after every change, so the screen shows what the server decided', async () => {
    const { server, tree } = await started()
    server.setPages(outline(['B', 'A']))

    await tree.move('B', { parentId: null, beforeId: 'A' })

    expect(tree.pages.map((p) => p.id)).toEqual(['B', 'A'])
  })

  it('tells the person a page went to the trash, with the title', async () => {
    const { server, tree, notices } = await started()

    await tree.trashPage('A1')

    expect(server.writes()).toEqual([
      { method: 'POST', path: '/api/pages/A1/trash', body: { expectedVersion: 1 } },
    ])
    expect(notices.notices).toMatchObject([{ type: 'trashed', pageId: 'A1', title: 'A1' }])
  })

  it('tells the person a page was restored, and where', async () => {
    const { tree, notices } = await started({ pages, trash: outline(['Gone']) })

    await tree.restore('Gone')

    expect(notices.notices).toMatchObject([
      { type: 'restored', pageId: 'Gone', title: 'Gone', toTopLevel: false },
    ])
  })

  it('says so when a restored page had to go to the top level', async () => {
    const { tree, notices } = await started({ pages, trash: outline(['Gone']), restoredTo: 'root' })

    await tree.restore('Gone')

    expect(notices.notices).toMatchObject([{ type: 'restored', toTopLevel: true }])
  })

  it('reports a refusal as a notice with its code, reads the tree again, and returns nothing', async () => {
    const { server, tree, notices } = await started()
    server.failNext(409, 'move_cycle')
    server.setPages(outline(['B']))

    const result = await tree.move('A', { parentId: 'A1' })

    expect(result).toBeNull()
    expect(notices.notices).toMatchObject([{ type: 'error', code: 'move_cycle' }])
    expect(tree.pages.map((p) => p.id)).toEqual(['B']) // what the server has now
    expect(notices.notices.some((n) => n.type === 'trashed')).toBe(false)
  })

  it('does not claim a trash happened when it failed', async () => {
    const { server, tree, notices } = await started()
    server.failNext(409, 'page_in_trash')

    await tree.trashPage('A')

    expect(notices.notices.map((n) => n.type)).toEqual(['error'])
  })

  it('keeps the wait of a rate limit with the error', async () => {
    const { server, tree, notices } = await started()
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) =>
        init?.method === 'PATCH'
          ? new Response('{"code":"too_many_requests"}', {
              status: 429,
              headers: { 'Content-Type': 'application/json', 'Retry-After': '30' },
            })
          : server.fetch(input, init),
      ),
    )

    await tree.rename('A', 'x')

    expect(notices.notices).toMatchObject([
      { type: 'error', code: 'too_many_requests', retryAfterSeconds: 30 },
    ])
  })

  it('reports a network failure too, without throwing', async () => {
    const { tree, notices } = await started()
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    await expect(tree.rename('A', 'x')).resolves.toBeNull()

    expect(notices.notices).toMatchObject([{ type: 'error', code: 'network_error' }])
  })

  it('does nothing without a workspace', async () => {
    const tree = useTreeStore()
    const server = fakeTree()

    expect(await tree.create('x')).toBeNull()
    expect(server.writes()).toEqual([])
  })
})
