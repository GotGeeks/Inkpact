import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { RouterView } from 'vue-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { jsonResponse, mountAt } from '../testing/helpers'
import { useNotificationsStore } from '../stores/notifications'
import { fakeTree, outline } from '../testing/treeFixtures'

const pages = outline(['Notes', '  Ideas', 'Work'])

let wrapper: VueWrapper | null = null

beforeEach(() => localStorage.clear())
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

function screen(wide: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === '(min-width: 48rem)' ? wide : query === '(pointer: fine)',
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }))
}

async function shell(
  path = '/',
  options: Parameters<typeof fakeTree>[0] = { pages },
  locale: 'en' | 'nl' = 'en',
) {
  const server = fakeTree(options)
  const mounted = await mountAt(RouterView, path, locale)
  wrapper = mounted.wrapper
  await flushPromises()
  await vi.waitFor(() => expect(wrapper!.find('main').text().length).toBeGreaterThan(0))
  return { server, ...mounted }
}

describe('AppShell on a wide screen', () => {
  it('shows the sidebar next to the page, with the workspace, the tree, the trash link and the person', async () => {
    screen(true)
    await shell()

    expect(wrapper!.find('aside').exists()).toBe(true)
    expect(wrapper!.get('[data-testid="workspace-name"]').text()).toBe('Private')
    expect(
      wrapper!.findAll('[role="treeitem"]').map((el) => el.attributes('data-page-id')),
    ).toEqual(['Notes', 'Work'])
    expect(wrapper!.find('a[href="/trash"]').exists()).toBe(true)
    expect(wrapper!.get('[data-testid="user-name"]').text()).toBe('Jos')
    expect(wrapper!.find('[aria-label="Open the page list"]').exists()).toBe(false)
  })

  it('shows the start page, which asks for a page to be picked', async () => {
    screen(true)
    await shell()

    expect(wrapper!.get('main h1').text()).toBe('Welcome')
    expect(wrapper!.get('main').text()).toContain('Pick a page in the sidebar')
  })

  it('invites the first page when there are none', async () => {
    screen(true)
    await shell('/', { pages: [] })

    expect(wrapper!.get('main').text()).toContain('You have no pages yet')
  })

  it('opens a page in the page area when it is clicked in the tree, and marks it', async () => {
    screen(true)
    const { router } = await shell()

    await wrapper!.get('[data-page-id="Work"]').trigger('click')
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('page'))
    await flushPromises()

    expect(router.currentRoute.value.params.pageId).toBe('Work')
    await vi.waitFor(() => expect(wrapper!.get('main h1').text()).toBe('Work'))
    expect(wrapper!.get('[data-page-id="Work"]').attributes('aria-selected')).toBe('true')
  })

  it('opens the branches above the page that is open, so it can be seen in the tree', async () => {
    screen(true)
    await shell('/p/Ideas')

    await vi.waitFor(() => expect(wrapper!.find('[data-page-id="Ideas"]').exists()).toBe(true))
    expect(wrapper!.get('[data-page-id="Ideas"]').attributes('aria-selected')).toBe('true')
  })

  it('counts what is in the trash next to its link', async () => {
    screen(true)
    await shell('/', { pages, trash: outline(['Old', 'Older']) })

    expect(wrapper!.get('[data-testid="trash-count"]').text()).toBe('2')
  })

  it('shows no count for an empty trash', async () => {
    screen(true)
    await shell()

    expect(wrapper!.find('[data-testid="trash-count"]').exists()).toBe(false)
  })

  it('does not offer to create pages to a viewer', async () => {
    screen(true)
    await shell('/', { pages, role: 'viewer' })

    expect(wrapper!.find('aside').text()).not.toContain('New page')
    expect(wrapper!.find('main').text()).not.toContain('New page')
  })

  it('speaks Dutch when the page is Dutch', async () => {
    screen(true)
    await shell('/', { pages }, 'nl')

    expect(wrapper!.get('main h1').text()).toBe('Welkom')
    expect(wrapper!.get('aside').text()).toContain('Nieuwe pagina')
    expect(wrapper!.get('aside').text()).toContain('Prullenbak')
  })
})

describe('AppShell when the pages cannot be loaded', () => {
  it('says so and tries again on request', async () => {
    screen(true)
    const server = fakeTree({ pages })
    const failing = server.fetch.getMockImplementation()!
    let fail = true
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) =>
      fail && String(input) === '/api/workspaces'
        ? jsonResponse(500, { code: 'server_error' })
        : failing(input, init),
    )
    const mounted = await mountAt(RouterView, '/')
    wrapper = mounted.wrapper
    await vi.waitFor(() => expect(wrapper!.find('[role="alert"]').exists()).toBe(true))

    expect(wrapper!.get('[role="alert"]').text()).toContain('The pages could not be loaded.')

    fail = false
    await wrapper!.get('[role="alert"] button').trigger('click')
    await vi.waitFor(() => expect(wrapper!.find('[role="alert"]').exists()).toBe(false))
    await vi.waitFor(() => expect(wrapper!.findAll('[role="treeitem"]').length).toBe(2))
  })
})

describe('AppShell on a narrow screen', () => {
  it('has a menu button instead of the sidebar, and opens the page list in a drawer', async () => {
    screen(false)
    await shell()

    expect(wrapper!.find('aside').exists()).toBe(false)
    expect(wrapper!.find('[role="tree"]').exists()).toBe(false)

    await wrapper!.get('button[aria-label="Open the page list"]').trigger('click')
    await flushPromises()

    const drawer = document.body.querySelector('[role="dialog"]')
    expect(drawer).not.toBeNull()
    expect(drawer!.querySelector('[role="tree"]')).not.toBeNull()
    expect(drawer!.querySelector('[data-page-id="Work"]')).not.toBeNull()
  })

  it('closes the drawer when a page is chosen, and shows it', async () => {
    screen(false)
    const { router } = await shell()
    await wrapper!.get('button[aria-label="Open the page list"]').trigger('click')
    await flushPromises()

    ;(document.body.querySelector('[role="dialog"] [data-page-id="Work"]') as HTMLElement).click()
    await vi.waitFor(() => expect(router.currentRoute.value.params.pageId).toBe('Work'))
    await flushPromises()

    expect(document.body.querySelector('[role="dialog"]')).toBeNull()
    await vi.waitFor(() => expect(wrapper!.get('main h1').text()).toBe('Work'))
  })

  it('closes the drawer when the trash is opened from it', async () => {
    screen(false)
    const { router } = await shell()
    await wrapper!.get('button[aria-label="Open the page list"]').trigger('click')
    await flushPromises()

    ;(document.body.querySelector('[role="dialog"] a[href="/trash"]') as HTMLElement).click()
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('trash'))
    await flushPromises()

    expect(document.body.querySelector('[role="dialog"]')).toBeNull()
  })

  it('closes the drawer when a page is trashed, so that the Undo notice can be reached', async () => {
    screen(false)
    await shell()
    await wrapper!.get('button[aria-label="Open the page list"]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[role="dialog"]')).not.toBeNull()

    useNotificationsStore().post({ type: 'trashed', pageId: 'Work', title: 'Work' })
    await flushPromises()

    expect(document.body.querySelector('[role="dialog"]')).toBeNull()
  })

  it('names the dialog for assistive technology', async () => {
    screen(false)
    await shell()

    await wrapper!.get('button[aria-label="Open the page list"]').trigger('click')
    await flushPromises()

    const dialog = document.body.querySelector('[role="dialog"]')!
    expect(dialog.textContent).toContain('Navigation')
  })
})
