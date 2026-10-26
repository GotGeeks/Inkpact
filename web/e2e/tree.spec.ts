import type { APIRequestContext } from '@playwright/test'
import { expect, test } from './fixtures'

interface Page {
  id: string
  parentId: string | null
  title: string
  sortKey: string
  version: number
}

// The tree has no screen yet (run 1.08), so this drives the API the way the client will: with the session and the
// CSRF token. Every project runs it at the same moment on the same workspace, so each test works inside a page of
// its own and only looks at that.
async function api(request: APIRequestContext) {
  const state = (await (await request.get('/api/auth/state')).json()) as { csrfToken: string }
  const headers = { 'X-CSRF-Token': state.csrfToken }
  const workspaces = (await (await request.get('/api/workspaces')).json()) as { id: string }[]
  const workspaceId = workspaces[0]!.id

  return {
    workspaceId,
    create: async (title: string, position: Record<string, string | null> = {}) => {
      const response = await request.post(`/api/workspaces/${workspaceId}/pages`, {
        headers,
        data: { title, ...position },
      })
      expect(response.status(), `creating ${title}`).toBe(201)
      return ((await response.json()) as { page: Page }).page
    },
    post: (path: string, data: object = {}) => request.post(path, { headers, data }),
    tree: async () =>
      (
        (await (await request.get(`/api/workspaces/${workspaceId}/pages`)).json()) as {
          pages: Page[]
        }
      ).pages,
    trash: async () =>
      (await (await request.get(`/api/workspaces/${workspaceId}/trash`)).json()) as Page[],
  }
}

test('pages can be created, ordered, moved, trashed and restored through the API', async ({
  request,
}, testInfo) => {
  const client = await api(request)
  const root = await client.create(`root ${testInfo.project.name} ${Date.now()}`)
  const first = await client.create('first', { parentId: root.id })
  const last = await client.create('last', { parentId: root.id })
  const middle = await client.create('middle', {
    parentId: root.id,
    afterId: first.id,
    beforeId: last.id,
  })

  const titlesUnderRoot = async () =>
    (await client.tree()).filter((page) => page.parentId === root.id).map((page) => page.title)
  expect(await titlesUnderRoot()).toEqual(['first', 'middle', 'last'])

  // Moving one page changes that page only, and sibling keys keep sorting as bytes.
  const before = (await client.tree()).filter((page) => page.parentId === root.id)
  expect(
    (
      await client.post(`/api/pages/${last.id}/move`, { parentId: root.id, beforeId: first.id })
    ).status(),
  ).toBe(200)
  expect(await titlesUnderRoot()).toEqual(['last', 'first', 'middle'])
  const after = (await client.tree()).filter((page) => page.parentId === root.id)
  for (const unchanged of [first, middle]) {
    expect(after.find((page) => page.id === unchanged.id)!.sortKey).toBe(
      before.find((page) => page.id === unchanged.id)!.sortKey,
    )
  }
  expect([...after.map((page) => page.sortKey)]).toEqual(
    [...after.map((page) => page.sortKey)].sort(),
  )

  // A page cannot go below itself.
  const cycle = await client.post(`/api/pages/${root.id}/move`, { parentId: middle.id })
  expect(cycle.status()).toBe(409)
  expect(await cycle.json()).toEqual({ code: 'move_cycle', field: 'parentId' })

  // Trashing the root takes its pages with it, and the trash shows the root only.
  expect((await client.post(`/api/pages/${root.id}/trash`)).status()).toBe(200)
  const visible = new Set((await client.tree()).map((page) => page.id))
  for (const page of [root, first, middle, last]) expect(visible.has(page.id)).toBe(false)
  const trash = (await client.trash()).map((page) => page.id)
  expect(trash).toContain(root.id)
  for (const page of [first, middle, last]) expect(trash).not.toContain(page.id)

  // A page below it cannot be restored alone; the root can, and brings everything back in order.
  expect((await client.post(`/api/pages/${first.id}/restore`)).status()).toBe(409)
  const restored = await client.post(`/api/pages/${root.id}/restore`)
  expect((await restored.json()).restoredTo).toBe('original')
  expect(await titlesUnderRoot()).toEqual(['last', 'first', 'middle'])
})

test('a stale version is applied and flagged, never refused', async ({ request }, testInfo) => {
  const client = await api(request)
  const page = await client.create(`versioned ${testInfo.project.name} ${Date.now()}`)

  const state = (await (await request.get('/api/auth/state')).json()) as { csrfToken: string }
  const rename = (title: string) =>
    request.patch(`/api/pages/${page.id}`, {
      headers: { 'X-CSRF-Token': state.csrfToken },
      data: { title, expectedVersion: 1 },
    })
  expect((await (await rename('from the laptop')).json()).conflict).toBe(false)
  const late = await (await rename('from the phone')).json()

  expect(late.conflict).toBe(true)
  expect(late.page).toMatchObject({ title: 'from the phone', version: 3 })
})
