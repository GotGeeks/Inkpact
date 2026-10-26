import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { RouterView } from 'vue-router'
import { mountAt } from '../testing/helpers'
import { fakeTree, outline } from '../testing/treeFixtures'

let wrapper: VueWrapper | null = null

beforeEach(() => localStorage.clear())
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

async function open(
  path: string,
  options: Parameters<typeof fakeTree>[0],
  locale: 'en' | 'nl' = 'en',
) {
  const server = fakeTree(options)
  const mounted = await mountAt(RouterView, path, locale)
  wrapper = mounted.wrapper
  await flushPromises()
  await vi.waitFor(() => expect(wrapper!.find('main').text().length).toBeGreaterThan(0))
  return { server, ...mounted }
}

describe('PageView', () => {
  it('shows the title as the heading, and a placeholder for the content that comes later', async () => {
    await open('/p/Notes', { pages: outline(['Notes']) })

    await vi.waitFor(() => expect(wrapper!.get('main h1').text()).toBe('Notes'))
    expect(wrapper!.get('main').text()).toContain('The page content will appear here.')
    expect(wrapper!.find('[role="status"]').exists()).toBe(false)
  })

  it('shows "Untitled" for a page with no title', async () => {
    await open('/p/x', { pages: [{ ...outline(['x'])[0]!, title: '' }] })

    await vi.waitFor(() => expect(wrapper!.get('main h1').text()).toBe('Untitled'))
  })

  it('says when a page cannot be found, and leads back', async () => {
    await open('/p/ghost', { pages: outline(['Notes']) })

    await vi.waitFor(() =>
      expect(wrapper!.get('main h1').text()).toBe('This page could not be found'),
    )
    expect(wrapper!.get('main a').attributes('href')).toBe('/')
  })

  it('says a page is in the trash and offers to restore it', async () => {
    const { server } = await open('/p/Old', {
      pages: [],
      trash: outline(['Old']).map((p) => ({ ...p, deletedAt: '2026-10-25T10:00:00Z' })),
    })

    await vi.waitFor(() =>
      expect(wrapper!.get('[role="status"]').text()).toContain('This page is in the trash.'),
    )
    await wrapper!.get('[role="status"] button').trigger('click')
    await flushPromises()

    expect(server.writes()).toEqual([{ method: 'POST', path: '/api/pages/Old/restore', body: {} }])
  })

  it('says when only a page above is in the trash, and does not offer to restore this one', async () => {
    await open('/p/Child', { pages: outline(['Parent', '  Child']), hidden: ['Child'] })

    await vi.waitFor(() => expect(wrapper!.find('[role="status"]').exists()).toBe(true))

    expect(wrapper!.get('[role="status"]').text()).toContain(
      'A page above this one is in the trash.',
    )
    expect(wrapper!.find('[role="status"] button').exists()).toBe(false)
  })

  it('offers no restore to a viewer', async () => {
    await open('/p/Old', {
      pages: [],
      role: 'viewer',
      trash: outline(['Old']).map((p) => ({ ...p, deletedAt: '2026-10-25T10:00:00Z' })),
    })

    await vi.waitFor(() => expect(wrapper!.find('[role="status"]').exists()).toBe(true))
    expect(wrapper!.find('[role="status"] button').exists()).toBe(false)
  })
})

describe('TrashView', () => {
  const trashed = outline(['Old', 'Older']).map((p, i) => ({
    ...p,
    deletedAt: `2026-10-2${5 - i}T10:00:00Z`,
  }))

  it('lists what is in the trash, with when it was moved there, in the language of the page', async () => {
    await open('/trash', { pages: [], trash: trashed })

    await vi.waitFor(() =>
      expect(wrapper!.findAll('[data-testid="trash-list"] li')).toHaveLength(2),
    )
    const text = wrapper!.get('[data-testid="trash-list"]').text()
    expect(text).toContain('Old')
    expect(text).toMatch(/Moved to the trash .*2026/)
  })

  it('formats the date for a Dutch page', async () => {
    await open('/trash', { pages: [], trash: trashed }, 'nl')

    await vi.waitFor(() => expect(wrapper!.find('[data-testid="trash-list"]').exists()).toBe(true))
    expect(wrapper!.get('[data-testid="trash-list"]').text()).toMatch(
      /Naar de prullenbak verplaatst op .*okt.*2026/,
    )
  })

  it('says when the trash is empty', async () => {
    await open('/trash', { pages: [], trash: [] })

    await vi.waitFor(() => expect(wrapper!.get('main').text()).toContain('The trash is empty.'))
    expect(wrapper!.find('[data-testid="trash-list"]').exists()).toBe(false)
  })

  it('restores a page with its button, which names the page', async () => {
    const { server } = await open('/trash', { pages: [], trash: trashed })
    await vi.waitFor(() =>
      expect(wrapper!.find('button[aria-label="Restore Older"]').exists()).toBe(true),
    )

    await wrapper!.get('button[aria-label="Restore Older"]').trigger('click')
    await flushPromises()

    expect(server.writes()).toEqual([
      { method: 'POST', path: '/api/pages/Older/restore', body: {} },
    ])
  })

  it('offers no restore to a viewer', async () => {
    await open('/trash', { pages: [], trash: trashed, role: 'viewer' })

    await vi.waitFor(() => expect(wrapper!.find('[data-testid="trash-list"]').exists()).toBe(true))
    expect(wrapper!.findAll('[data-testid="trash-list"] button')).toHaveLength(0)
  })
})

describe('StartPage', () => {
  it('creates a page and goes to it', async () => {
    const { server, router } = await open('/', { pages: [] })
    await vi.waitFor(() => expect(wrapper!.find('main button').exists()).toBe(true))

    await wrapper!.get('main button').trigger('click')
    await flushPromises()

    expect(server.writes()[0]).toMatchObject({
      method: 'POST',
      path: '/api/workspaces/w1/pages',
      body: { title: '', parentId: null },
    })
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('page'))
  })
})
