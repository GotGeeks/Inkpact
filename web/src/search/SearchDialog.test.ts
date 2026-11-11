import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { RouterView } from 'vue-router'
import { SEARCH_DEBOUNCE_MS, useSearchStore } from '../stores/search'
import { jsonResponse, mountAt } from '../testing/helpers'
import { registerLinkShortcut } from '../editor/linkShortcutRegistry'
import { fakeTree, outline } from '../testing/treeFixtures'
import { useNotificationsStore } from '../stores/notifications'
import type { SearchHit, SearchResponse, SearchSegment } from './types'

const pages = outline(['Notebook', '  Weekly meeting', 'Ideas'])

let wrapper: VueWrapper | null = null

beforeEach(() => {
  localStorage.clear()
  vi.useFakeTimers()
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

const plain = (text: string): SearchSegment[] => (text ? [{ text, match: false }] : [])
const hit = (id: string, title: SearchSegment[], snippet: SearchSegment[] = []): SearchHit => ({
  id,
  title,
  snippet,
})
const meeting = hit(
  'Weekly meeting',
  [
    { text: 'Weekly ', match: false },
    { text: 'meeting', match: true },
  ],
  [
    { text: 'We had a ', match: false },
    { text: 'meeting', match: true },
    { text: ' about it.', match: false },
  ],
)

type Options = Parameters<typeof fakeTree>[0]
type ExtraOptions = Omit<NonNullable<Options>, 'pages' | 'search'>

async function shell(
  search: NonNullable<Options>['search'],
  locale: 'en' | 'nl' = 'en',
  path = '/',
  extra: ExtraOptions = {},
  attach = false,
) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === '(min-width: 48rem)',
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }))
  const server = fakeTree({ pages, search, ...extra })
  const mounted = await mountAt(RouterView, path, locale, { attach })
  wrapper = mounted.wrapper
  await flushPromises()
  await vi.waitFor(() => expect(wrapper!.find('main').text().length).toBeGreaterThan(0))
  return { server, ...mounted }
}

const answer = (...results: SearchHit[]): SearchResponse => ({ results, more: false })

const dialog = () => document.body.querySelector('[data-testid="search-dialog"]')
/** The phone's drawer: a dialog that is not the search. */
const drawer = () =>
  document.body.querySelector('[role="dialog"]:not([data-testid="search-dialog"])')
const input = () => document.body.querySelector<HTMLInputElement>('[data-testid="search-input"]')!
const options = () => [...document.body.querySelectorAll('[role="option"]')]
async function chooseTab(scope: 'pages' | 'tags' | 'commands') {
  const tab = document.body.querySelector<HTMLButtonElement>(
    `[data-testid="palette-tab-${scope}"]`,
  )!
  tab.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: 0 }))
  await flushPromises()
}
/** Which result is highlighted, as the listbox marks it. */
const active = () => options().findIndex((o) => o.hasAttribute('data-highlighted'))

async function openByShortcut(init: KeyboardEventInit = { ctrlKey: true }) {
  window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', bubbles: true, ...init }))
  await flushPromises()
}

function includeNewPageInTree(server: ReturnType<typeof fakeTree>, wait?: Promise<void>) {
  const original = server.fetch.getMockImplementation()!
  server.fetch.mockImplementation(async (input, init) => {
    const response = await original(input, init)
    if (response.ok && init?.method === 'POST' && String(input) === '/api/workspaces/w1/pages') {
      await wait
      server.setPages([
        ...pages,
        {
          id: 'new',
          parentId: null,
          title: '',
          sortKey: 'k9',
          version: 1,
          updatedAt: '2026-11-09T12:00:00Z',
        },
      ])
    }
    return response
  })
}

async function type(text: string) {
  await typeInto(input(), text)
}

async function typeInto(field: HTMLInputElement, text: string) {
  field.value = text
  field.dispatchEvent(new Event('input', { bubbles: true }))
  await flushPromises()
}

/** Types and waits for the search that follows the pause after the last key. */
async function typeAndSearch(text: string) {
  await type(text)
  await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS)
  await flushPromises()
}

const press = async (key: string) => {
  input().dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  await flushPromises()
}

describe('opening the search', () => {
  it('opens pages with Ctrl+K and Cmd+K, and commands with Ctrl+Alt+K', async () => {
    await shell(() => answer())

    await openByShortcut({ altKey: true, ctrlKey: true })
    expect(dialog()).not.toBeNull()
    expect(document.body.querySelector('[role="tab"][aria-selected="true"]')?.textContent).toBe(
      'Commands',
    )
    document.body
      .querySelector<HTMLButtonElement>('button[aria-label="Close quick switcher"]')!
      .click()
    await flushPromises()

    await openByShortcut({ shiftKey: true, ctrlKey: true })
    await openByShortcut({})
    expect(dialog()).toBeNull()

    await openByShortcut({ ctrlKey: true })
    expect(dialog()).not.toBeNull()
    expect(document.body.querySelector('[role="tab"][aria-selected="true"]')?.textContent).toBe(
      'Pages',
    )
    document.body
      .querySelector<HTMLButtonElement>('button[aria-label="Close quick switcher"]')!
      .click()
    await flushPromises()
    expect(dialog()).toBeNull()

    await openByShortcut({ metaKey: true })
    expect(dialog()).not.toBeNull()
  })

  it('leaves the key to the editor when the editor takes it (selected text, or a link), and opens search again once it does not', async () => {
    await shell(() => answer())
    let taken = true
    const stop = registerLinkShortcut(() => taken)
    const event = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, cancelable: true })

    window.dispatchEvent(event)
    await flushPromises()

    expect(dialog()).toBeNull()
    // The shell did not take the key: the editor, which gets it next, does.
    expect(event.defaultPrevented).toBe(false)

    taken = false
    await openByShortcut({ ctrlKey: true })
    expect(dialog()).not.toBeNull()
    stop()
  })

  it('takes the key from the page, so that the browser does not use it', async () => {
    await shell(() => answer())
    const event = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, cancelable: true })

    window.dispatchEvent(event)

    expect(event.defaultPrevented).toBe(true)
  })

  it('opens from the Search row of the sidebar and puts the cursor in the field', async () => {
    await shell(() => answer())

    await wrapper!.get('[data-testid="search-row"]').trigger('click')
    await flushPromises()

    expect(dialog()).not.toBeNull()
    expect(document.activeElement).toBe(input())
  })

  it('has a name, a label for the field and a description, in English and in Dutch', async () => {
    await shell(() => answer(), 'nl')
    await openByShortcut()

    expect(
      document.body.querySelector('[role="dialog"]')!.getAttribute('aria-labelledby'),
    ).toBeTruthy()
    expect(input().getAttribute('aria-label')).toBe('Pagina’s doorzoeken')
    expect(input().getAttribute('placeholder')).toBe(
      'Zoek pagina’s of kies een favoriet of onlangs geopende pagina…',
    )
    expect(document.body.textContent).toContain('Nog geen favorieten of onlangs geopende pagina’s.')
  })
})

describe('searching as you type', () => {
  it('asks for more letters under two, and does not search', async () => {
    const { server } = await shell(() => answer(meeting))
    await openByShortcut()

    await typeAndSearch('m')

    expect(document.body.querySelector('[data-testid="search-hint"]')!.textContent).toContain(
      'Type at least 2 letters to search.',
    )
    expect(server.searches()).toEqual([])
  })

  describe('palette scopes', () => {
    it('shows ordered favorites and recent pages once, with favorite pages omitted from Recent', async () => {
      await shell(() => answer(), 'en', '/', {
        favorites: ['Ideas', 'Weekly meeting'],
        recent: [
          { pageId: 'Ideas', openedAt: '2026-11-09T11:00:00Z' },
          { pageId: 'Notebook', openedAt: '2026-11-09T10:00:00Z' },
        ],
      })

      await openByShortcut()
      await vi.waitFor(() => expect(options()).toHaveLength(3))

      expect(options().map((option) => option.getAttribute('data-page-id'))).toEqual([
        'Ideas',
        'Weekly meeting',
        'Notebook',
      ])
      // Groups of the listbox, each named by its label (a section or heading inside a listbox breaks its semantics).
      const groupName = (testId: string) => {
        const group = document.body.querySelector(`[data-testid="${testId}"]`)!
        expect(group.getAttribute('role')).toBe('group')
        return document.getElementById(group.getAttribute('aria-labelledby')!)?.textContent?.trim()
      }
      expect(groupName('palette-favorites')).toBe('Favorites')
      expect(groupName('palette-recent')).toBe('Recently opened')
      expect(
        document.body.querySelector('[data-testid="palette-favorites"] [data-page-id="Ideas"]'),
      ).not.toBeNull()
    })

    it('loads tags only when selected, filters them by accent-folded name and opens the tag route', async () => {
      const { server, router } = await shell(() => answer(), 'en', '/', {
        tags: {
          Ideas: [{ id: 't1', name: 'Café' }],
          'Weekly meeting': [{ id: 't2', name: 'Work' }],
        },
      })

      expect(server.fetch.mock.calls.some((call) => String(call[0]).endsWith('/tags'))).toBe(false)
      await openByShortcut()
      await chooseTab('tags')
      await flushPromises()
      await vi.waitFor(() =>
        expect(document.body.querySelector('[data-testid="palette-input-tags"]')).not.toBeNull(),
      )
      await typeInto(
        document.body.querySelector<HTMLInputElement>('[data-testid="palette-input-tags"]')!,
        'CAFE',
      )

      expect(options().map((option) => option.textContent)).toEqual(['Café1 page'])
      expect(server.searches()).toEqual([])
      ;(options()[0] as HTMLElement).click()

      await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('tag'))
      expect(router.currentRoute.value.params.tagId).toBe('t1')
      expect(dialog()).toBeNull()
    })

    it('does not replace the saved page query when switching to tags and commands', async () => {
      const { server } = await shell(() => answer(meeting))
      await openByShortcut()
      await typeAndSearch('meeting')

      await chooseTab('tags')
      await typeInto(
        document.body.querySelector<HTMLInputElement>('[data-testid="palette-input-tags"]')!,
        'work',
      )
      await chooseTab('commands')
      await typeInto(
        document.body.querySelector<HTMLInputElement>('[data-testid="palette-input-commands"]')!,
        'start',
      )
      await chooseTab('pages')

      expect(input().value).toBe('meeting')
      expect(server.searches()).toEqual(['meeting', 'meeting'])
    })

    it('surfaces and retries favorite and tag list errors in their own scopes', async () => {
      const server = fakeTree({
        pages,
        favorites: ['Ideas'],
        tags: { Ideas: [{ id: 't1', name: 'Work' }] },
      })
      const original = server.fetch.getMockImplementation()!
      let favoritesFail = true
      let tagsFail = true
      server.fetch.mockImplementation(async (request, init) => {
        const path = String(request)
        if (favoritesFail && path.endsWith('/favorites'))
          return jsonResponse(503, { code: 'server_error' })
        if (tagsFail && path.endsWith('/tags')) return jsonResponse(503, { code: 'server_error' })
        return original(request, init)
      })
      const mounted = await mountAt(RouterView, '/', 'en')
      wrapper = mounted.wrapper
      await flushPromises()
      await vi.waitFor(() => expect(wrapper!.find('main').text().length).toBeGreaterThan(0))
      await openByShortcut()
      await vi.waitFor(() =>
        expect(document.body.textContent).toContain('Favorites could not be loaded.'),
      )

      favoritesFail = false
      document.body.querySelector<HTMLButtonElement>('[role="alert"] button')!.click()
      await vi.waitFor(() =>
        expect(options().map((option) => option.getAttribute('data-page-id'))).toContain('Ideas'),
      )

      await chooseTab('tags')
      await vi.waitFor(() =>
        expect(document.body.textContent).toContain('Tags could not be loaded.'),
      )
      tagsFail = false
      document.body.querySelector<HTMLButtonElement>('[role="alert"] button')!.click()
      await vi.waitFor(() =>
        expect(options().some((option) => option.textContent?.includes('Work'))).toBe(true),
      )
    })

    it('does not show stale favorites or recents after both refreshes fail', async () => {
      const server = fakeTree({
        pages,
        favorites: ['Ideas'],
        recent: [{ pageId: 'Weekly meeting', openedAt: '2026-11-09T11:00:00Z' }],
      })
      const original = server.fetch.getMockImplementation()!
      let listsFail = false
      server.fetch.mockImplementation(async (request, init) => {
        const path = String(request)
        if (listsFail && (path.endsWith('/favorites') || path.endsWith('/recent'))) {
          return jsonResponse(503, { code: 'server_error' })
        }
        return original(request, init)
      })
      const mounted = await mountAt(RouterView, '/', 'en')
      wrapper = mounted.wrapper
      await flushPromises()
      await vi.waitFor(() => expect(wrapper!.find('main').text().length).toBeGreaterThan(0))

      await openByShortcut()
      await vi.waitFor(() =>
        expect(options().map((option) => option.getAttribute('data-page-id'))).toEqual([
          'Ideas',
          'Weekly meeting',
        ]),
      )
      document.body
        .querySelector<HTMLButtonElement>('button[aria-label="Close quick switcher"]')!
        .click()
      await flushPromises()

      listsFail = true
      await openByShortcut()
      await vi.waitFor(() =>
        expect(document.body.textContent).toContain('Favorites could not be loaded.'),
      )
      await vi.waitFor(() =>
        expect(document.body.textContent).toContain('Recently opened pages could not be loaded.'),
      )
      expect(options().filter((option) => option.hasAttribute('data-page-id'))).toEqual([])
    })

    it('does not refresh the tree when list reads finish after the palette closes', async () => {
      const { server } = await shell(() => answer(), 'en', '/', { favorites: ['Missing'] })
      const original = server.fetch.getMockImplementation()!
      let release!: () => void
      const listsPending = new Promise<void>((resolve) => (release = resolve))
      let listReads = 0
      server.fetch.mockImplementation(async (request, init) => {
        const path = String(request)
        if (path.endsWith('/favorites') || path.endsWith('/recent')) {
          listReads++
          await listsPending
        }
        return original(request, init)
      })
      const pageTreeReads = () =>
        server.fetch.mock.calls.filter((call) => String(call[0]).endsWith('/workspaces/w1/pages'))
          .length

      await openByShortcut()
      await vi.waitFor(() => expect(listReads).toBe(2))
      document.body
        .querySelector<HTMLButtonElement>('button[aria-label="Close quick switcher"]')!
        .click()
      await flushPromises()
      const readsWhenClosed = pageTreeReads()

      release()
      await flushPromises()
      await flushPromises()

      expect(pageTreeReads()).toBe(readsWhenClosed)
    })

    it('refreshes the tree when favorites contain a page it has not loaded', async () => {
      const { server } = await shell(() => answer(), 'en', '/', {
        favorites: ['Missing', 'Ideas'],
      })
      const readsBefore = server.fetch.mock.calls.filter((call) =>
        String(call[0]).endsWith('/workspaces/w1/pages'),
      ).length
      await openByShortcut()
      await vi.waitFor(() =>
        expect(options().map((option) => option.getAttribute('data-page-id'))).toEqual(['Ideas']),
      )

      const readsAfter = server.fetch.mock.calls.filter((call) =>
        String(call[0]).endsWith('/workspaces/w1/pages'),
      ).length
      expect(readsAfter).toBeGreaterThan(readsBefore)
    })

    it('surfaces and retries a failed tree refresh for missing favorite ids', async () => {
      const { server } = await shell(() => answer(), 'en', '/', { favorites: ['Missing'] })
      const original = server.fetch.getMockImplementation()!
      let treeFail = true
      server.fetch.mockImplementation(async (request, init) => {
        if (treeFail && String(request).endsWith('/workspaces/w1/pages'))
          return jsonResponse(503, { code: 'server_error' })
        return original(request, init)
      })
      await openByShortcut()
      await vi.waitFor(() =>
        expect(document.body.textContent).toContain('The page list could not be refreshed.'),
      )

      treeFail = false
      document.body.querySelector<HTMLButtonElement>('[role="alert"] button')!.click()
      await vi.waitFor(() =>
        expect(document.body.querySelector('[data-testid="palette-pages-empty"]')).not.toBeNull(),
      )
    })

    it('offers only permission-appropriate commands and executes available navigation commands', async () => {
      const { router } = await shell(() => answer(), 'en', '/', { role: 'viewer' })
      await openByShortcut({ ctrlKey: true, altKey: true })

      const names = options().map((option) => option.textContent)
      expect(names.some((name) => name?.includes('Create new page'))).toBe(false)
      expect(names.some((name) => name?.includes('Import from Joplin'))).toBe(false)

      ;(
        options().find((option) => option.textContent?.includes('Open tags')) as HTMLElement
      ).click()
      await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('tags'))

      expect(dialog()).toBeNull()
    })

    it('lets a viewer favorite the live current page using the existing store action', async () => {
      const { server } = await shell(
        () => answer(),
        'en',
        '/p/Weekly meeting',
        { role: 'viewer', favorites: [] },
        true,
      )
      await vi.waitFor(() =>
        expect(wrapper!.find('[data-testid="page-title"]').exists()).toBe(true),
      )
      await openByShortcut({ ctrlKey: true, altKey: true })
      await vi.waitFor(() =>
        expect(
          options().some((option) => option.textContent?.includes('Add current page to favorites')),
        ).toBe(true),
      )

      ;(options().find((option) =>
        option.textContent?.includes('Add current page to favorites'),
      ) as HTMLElement | undefined)!.click()
      await vi.waitFor(() =>
        expect(
          server.writes().some((write) => write.path === '/api/pages/Weekly%20meeting/favorite'),
        ).toBe(true),
      )
      expect(server.favorites()).toContain('Weekly meeting')
    })

    it('does not offer the favorite command for a trashed page', async () => {
      const { server } = await shell(
        () => answer(),
        'en',
        '/p/Gone',
        { trash: outline(['Gone']), favorites: [] },
        true,
      )
      await vi.waitFor(() =>
        expect(wrapper!.find('[data-testid="page-title"]').exists()).toBe(true),
      )
      await openByShortcut({ ctrlKey: true, altKey: true })

      expect(options().some((option) => option.textContent?.includes('current page'))).toBe(false)
      expect(server.searches()).toEqual([])
    })
  })

  describe('palette commands', () => {
    it('creates one top-level page from the direct shortcut', async () => {
      const { server, router } = await shell(() => answer(), 'en', '/', {}, true)
      includeNewPageInTree(server)

      window.dispatchEvent(
        new KeyboardEvent('keydown', {
          key: 'n',
          code: 'KeyN',
          ctrlKey: true,
          altKey: true,
          bubbles: true,
          cancelable: true,
        }),
      )

      await vi.waitFor(() => expect(router.currentRoute.value.params.pageId).toBe('new'))
      expect(
        server.writes().filter((write) => write.path === '/api/workspaces/w1/pages'),
      ).toHaveLength(1)
    })

    it('creates one top-level page from the palette and focuses its title', async () => {
      const { server, router } = await shell(() => answer(), 'en', '/', {}, true)
      includeNewPageInTree(server)
      await openByShortcut({ ctrlKey: true, altKey: true })

      ;(options()[0] as HTMLElement).click()

      await vi.waitFor(() => expect(server.writes().length).toBeGreaterThan(0))
      expect(
        server.writes().filter((write) => write.path === '/api/workspaces/w1/pages'),
      ).toHaveLength(1)
      await vi.waitFor(() => expect(router.currentRoute.value.params.pageId).toBe('new'))
      const title = wrapper!.get('[data-testid="page-title"]').element
      await vi.waitFor(() => expect(document.activeElement).toBe(title))
      expect(server.writes()[0]).toEqual({
        method: 'POST',
        path: '/api/workspaces/w1/pages',
        body: { title: '', parentId: null, afterId: null, beforeId: null },
      })
    })

    it('does not interrupt navigation or focus while creating, and offers an Open action', async () => {
      let release!: () => void
      const wait = new Promise<void>((resolve) => (release = resolve))
      const { server, router } = await shell(() => answer(), 'en', '/', {}, true)
      includeNewPageInTree(server, wait)
      await openByShortcut({ ctrlKey: true, altKey: true })
      ;(options()[0] as HTMLElement).click()
      await vi.waitFor(() => expect(server.writes()).toHaveLength(1))

      const otherControl = document.createElement('button')
      document.body.append(otherControl)
      otherControl.focus()
      release()

      await flushPromises()
      await vi.waitFor(() =>
        expect(useNotificationsStore().notices).toMatchObject([
          { type: 'pageCreated', pageId: 'new', title: '' },
        ]),
      )
      await vi.waitFor(() => expect(wrapper!.text()).toContain('Created “Untitled”.'))
      expect(router.currentRoute.value.name).toBe('home')
      expect(document.activeElement).toBe(otherControl)
      ;[...document.querySelectorAll<HTMLButtonElement>('button')]
        .find((button) => button.textContent?.trim() === 'Open')!
        .click()
      await vi.waitFor(() => expect(router.currentRoute.value.params.pageId).toBe('new'))
    })

    it('opens Commands with Ctrl+Alt+K from the editor, and switches to them from the palette field', async () => {
      await shell(() => answer())
      const editor = document.createElement('div')
      editor.contentEditable = 'true'
      document.body.append(editor)
      editor.focus()
      const key = { key: 'k', code: 'KeyK', ctrlKey: true, altKey: true, bubbles: true }
      const fromEditor = new KeyboardEvent('keydown', { ...key, cancelable: true })
      editor.dispatchEvent(fromEditor)
      await flushPromises()
      expect(fromEditor.defaultPrevented).toBe(true)
      expect(document.body.querySelector('[role="tab"][aria-selected="true"]')?.textContent).toBe(
        'Commands',
      )

      useSearchStore().setScope('pages')
      await flushPromises()
      input().dispatchEvent(new KeyboardEvent('keydown', key))
      await flushPromises()
      expect(document.body.querySelector('[role="tab"][aria-selected="true"]')?.textContent).toBe(
        'Commands',
      )
      editor.remove()
    })

    it('leaves Ctrl+Alt+K to the text when the layout types a character with it (AltGr)', async () => {
      await shell(() => answer())
      const field = document.createElement('input')
      document.body.append(field)
      field.focus()
      const event = new KeyboardEvent('keydown', {
        key: 'k',
        code: 'KeyK',
        ctrlKey: true,
        altKey: true,
        bubbles: true,
        cancelable: true,
      })
      vi.spyOn(event, 'getModifierState').mockImplementation((name) => name === 'AltGraph')
      field.dispatchEvent(event)
      await flushPromises()
      expect(event.defaultPrevented).toBe(false)
      expect(dialog()).toBeNull()
      field.remove()
    })

    it('does not run the new-page shortcut from an input or while a dialog is open', async () => {
      const { server } = await shell(() => answer())
      await openByShortcut()
      input().dispatchEvent(
        new KeyboardEvent('keydown', { key: 'n', ctrlKey: true, altKey: true, bubbles: true }),
      )
      expect(server.writes()).toEqual([])

      dialog()!.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'n', ctrlKey: true, altKey: true, bubbles: true }),
      )
      expect(server.writes()).toEqual([])
    })

    it('only starts one create when a shortcut is pressed while a palette create is pending', async () => {
      let release!: () => void
      const wait = new Promise<void>((resolve) => (release = resolve))
      const { server } = await shell(() => answer())
      includeNewPageInTree(server, wait)
      await openByShortcut({ ctrlKey: true, altKey: true })
      ;(options()[0] as HTMLElement).click()
      await vi.waitFor(() => expect(server.writes()).toHaveLength(1))

      window.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'n', ctrlKey: true, altKey: true, bubbles: true }),
      )
      expect(server.writes()).toHaveLength(1)
      release()
    })
  })

  it('searches 150 ms after the last key, once', async () => {
    const { server } = await shell(() => answer(meeting))
    await openByShortcut()

    await type('me')
    await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS / 2)
    await type('mee')
    await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS / 2)
    expect(server.searches()).toEqual([])
    await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS)
    await flushPromises()

    expect(server.searches()).toEqual(['mee'])
    expect(options()).toHaveLength(1)
  })

  it('sends the text as typed, trimmed, and encoded', async () => {
    const { server } = await shell(() => answer())
    await openByShortcut()

    await typeAndSearch('  café & ko  ')

    expect(server.searches()).toEqual(['café & ko'])
  })

  it('marks the matches as text in mark elements, in the title and in the snippet', async () => {
    await shell(() => answer(meeting))
    await openByShortcut()

    await typeAndSearch('meet')

    const marks = [...document.body.querySelectorAll('[role="option"] mark')]
    expect(marks.map((mark) => mark.textContent)).toEqual(['meeting', 'meeting'])
    expect(document.body.querySelector('[data-testid="search-snippet"]')!.textContent).toContain(
      'We had a meeting about it.',
    )
  })

  it('never turns what a page holds into markup', async () => {
    const hostile = hit(
      'Ideas',
      [{ text: '<img src=x onerror=alert(1)>', match: true }],
      [
        { text: '<b>bold</b>', match: false },
        { text: '<script>x</script>', match: true },
      ],
    )
    await shell(() => answer(hostile))
    await openByShortcut()

    await typeAndSearch('ideas')

    const option = options()[0]!
    expect(option.querySelector('img, b, script')).toBeNull()
    expect(option.textContent).toContain('<img src=x onerror=alert(1)>')
    expect(option.textContent).toContain('<script>x</script>')
  })

  it('shows the pages above a result, as the sidebar has them, and Untitled for a page without a title', async () => {
    await shell(() => answer(meeting, hit('Ideas', [], [])))
    await openByShortcut()

    await typeAndSearch('me')

    const rows = options()
    expect(rows[0]!.querySelector('[data-testid="search-path"]')!.textContent).toBe('Notebook')
    expect(rows[1]!.querySelector('[data-testid="search-path"]')).toBeNull()
    expect(rows[1]!.textContent).toContain('Untitled')
  })

  it('says nothing was found, for the text that was searched', async () => {
    await shell(() => answer())
    await openByShortcut()

    await typeAndSearch('zebra')

    expect(document.body.querySelector('[data-testid="search-empty"]')!.textContent).toBe(
      'Nothing found for “zebra”.',
    )
  })

  it('says so in Dutch', async () => {
    await shell(() => answer(), 'nl')
    await openByShortcut()

    await typeAndSearch('zebra')

    expect(document.body.querySelector('[data-testid="search-empty"]')!.textContent).toBe(
      'Niets gevonden voor “zebra”.',
    )
  })

  it('says a connection is needed when the search fails', async () => {
    await shell(() => {
      throw new Error('offline')
    })
    await openByShortcut()

    await typeAndSearch('meeting')

    expect(document.body.querySelector('[data-testid="search-error"]')!.textContent).toContain(
      'Search needs a connection to the server.',
    )
  })

  it('says what the server said when it refuses, not that the connection is gone', async () => {
    await shell(() => jsonResponse(500, { code: 'server_error' }))
    await openByShortcut()

    await typeAndSearch('meeting')

    expect(document.body.querySelector('[data-testid="search-error"]')!.textContent).toContain(
      'Something went wrong on the server.',
    )
  })

  it('drops the old list when a search fails, so it cannot be opened as the answer to the new text', async () => {
    const { router } = await shell((query) => {
      if (query === 'meet') return answer(meeting)
      throw new Error('offline')
    })
    await openByShortcut()
    await typeAndSearch('meet')
    expect(options()).toHaveLength(1)

    await typeAndSearch('meeting')
    await press('Enter')

    expect(options()).toHaveLength(0)
    expect(document.body.querySelector('[data-testid="search-error"]')).not.toBeNull()
    expect(router.currentRoute.value.name).toBe('home')
  })

  it('says when more pages matched than are listed', async () => {
    await shell(() => ({ results: [meeting], more: true }))
    await openByShortcut()

    await typeAndSearch('me')

    expect(document.body.querySelector('[data-testid="search-more"]')!.textContent).toBe(
      'Showing the first 1 result. Type more to narrow it down.',
    )
  })

  it('keeps the old results while the new ones load, and shows only the answer to the newest text', async () => {
    const waiting = new Map<string, (response: SearchResponse) => void>()
    await shell((query) =>
      query === 'me'
        ? answer(meeting)
        : new Promise<SearchResponse>((resolve) => waiting.set(query, resolve)),
    )
    await openByShortcut()
    await typeAndSearch('me')
    expect(options()).toHaveLength(1)

    await typeAndSearch('mee')
    await typeAndSearch('meet')
    // The old list is still there, and busy.
    expect(options()).toHaveLength(1)
    expect(document.body.querySelector('[role="listbox"]')!.getAttribute('aria-busy')).toBe('true')

    // The answer to the newest text arrives first, and the older one late: it is ignored.
    waiting.get('meet')!(answer(hit('Ideas', plain('Ideas'))))
    await flushPromises()
    waiting.get('mee')!(answer(meeting, meeting))
    await flushPromises()

    expect(options().map((o) => o.getAttribute('data-page-id'))).toEqual(['Ideas'])
  })

  it('cancels the search that is on its way when the text changes', async () => {
    const signals: (AbortSignal | undefined)[] = []
    await shell((_, signal) => {
      signals.push(signal)
      return new Promise<SearchResponse>(() => undefined)
    })
    await openByShortcut()

    await typeAndSearch('mee')
    await typeAndSearch('meet')

    expect(signals[0]!.aborted).toBe(true)
    expect(signals[1]!.aborted).toBe(false)
  })

  it('drops the list when the text gets too short again', async () => {
    await shell(() => answer(meeting))
    await openByShortcut()
    await typeAndSearch('meet')
    expect(options()).toHaveLength(1)

    await typeAndSearch('m')

    expect(options()).toHaveLength(0)
    expect(document.body.querySelector('[data-testid="search-hint"]')).not.toBeNull()
  })
})

describe('choosing a page', () => {
  it('moves through the results with the arrow keys and wraps around', async () => {
    await shell(() =>
      answer(meeting, hit('Ideas', plain('Ideas')), hit('Notebook', plain('Notebook'))),
    )
    await openByShortcut()
    await typeAndSearch('id')
    expect(active()).toBe(0)
    expect(input().getAttribute('aria-activedescendant')).toBe(options()[0]!.id)

    await press('ArrowDown')
    await press('ArrowDown')
    expect(active()).toBe(2)
    await press('ArrowDown')
    expect(active()).toBe(0)
    await press('ArrowUp')
    expect(active()).toBe(2)
    expect(input().getAttribute('aria-activedescendant')).toBe(options()[2]!.id)
    expect(input().getAttribute('aria-expanded')).toBe('true')
    // The cursor stays in the field all the while.
    expect(document.activeElement).toBe(input())
  })

  it('goes to the first and the last result with Home and End', async () => {
    await shell(() =>
      answer(meeting, hit('Ideas', plain('Ideas')), hit('Notebook', plain('Notebook'))),
    )
    await openByShortcut()
    await typeAndSearch('id')

    await press('End')
    expect(active()).toBe(2)
    await press('Home')
    expect(active()).toBe(0)
  })

  it('points the field at no result once nothing is found, not at one of the list before', async () => {
    await shell((query) =>
      query.includes('zebra') ? answer() : answer(meeting, hit('Ideas', plain('Ideas'))),
    )
    await openByShortcut()
    await typeAndSearch('id')
    expect(input().getAttribute('aria-activedescendant')).toBe(options()[0]!.id)

    await typeAndSearch('zebra')

    expect(options()).toEqual([])
    // A reference to an element that is gone is an error to assistive technology (WCAG 4.1.2).
    expect(input().hasAttribute('aria-activedescendant')).toBe(false)
  })

  it('points the field at the list also before there is a result', async () => {
    await shell(() => answer())
    await openByShortcut()

    const list = document.getElementById(input().getAttribute('aria-controls')!)
    expect(list?.getAttribute('role')).toBe('listbox')
    expect(input().getAttribute('aria-expanded')).toBe('false')
  })

  it('does not scroll the list when the pointer moves over a result', async () => {
    const scroll = vi.spyOn(Element.prototype, 'scrollIntoView')
    await shell(() => answer(meeting, hit('Ideas', plain('Ideas'))))
    await openByShortcut()
    await typeAndSearch('id')
    scroll.mockClear()

    options()[1]!.dispatchEvent(new PointerEvent('pointermove', { bubbles: true }))
    await flushPromises()

    expect(active()).toBe(1)
    expect(scroll).not.toHaveBeenCalled()
    scroll.mockRestore()
  })

  it('opens the first result of the new text on an Enter pressed before it is there, not one of the old list', async () => {
    let release: (() => void) | undefined
    const { router } = await shell((query) =>
      query === 'idea'
        ? answer(meeting)
        : new Promise<SearchResponse>((resolve) => {
            release = () => resolve(answer(hit('Ideas', plain('Ideas'))))
          }),
    )
    await openByShortcut()
    await typeAndSearch('idea')
    expect(options()).toHaveLength(1)

    // Typed on, and Enter at once: the old list (for "idea") is still on screen.
    await type('ideas')
    await press('Enter')
    expect(router.currentRoute.value.name).toBe('home')
    expect(dialog()).not.toBeNull()

    release!()
    await vi.waitFor(() => expect(router.currentRoute.value.params.pageId).toBe('Ideas'))
    expect(dialog()).toBeNull()
  })

  it('forgets an early Enter when the text changes again before the answer', async () => {
    const { router } = await shell(() => new Promise<SearchResponse>(() => undefined))
    await openByShortcut()
    await type('ideas')
    await press('Enter')

    await type('idea')
    await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS)
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('home')
  })

  it('opens the chosen page with Enter, closes the dialog and keeps the search for next time', async () => {
    const { router } = await shell(() => answer(meeting, hit('Ideas', plain('Ideas'))))
    await openByShortcut()
    await typeAndSearch('meeting')
    await press('ArrowDown')

    await press('Enter')
    await vi.waitFor(() => expect(router.currentRoute.value.params.pageId).toBe('Ideas'))
    expect(dialog()).toBeNull()

    await openByShortcut()
    expect(input().value).toBe('meeting')
    expect(options()).toHaveLength(2)
  })

  it('opens the page that is clicked', async () => {
    const { router } = await shell(() => answer(meeting))
    await openByShortcut()
    await typeAndSearch('meeting')

    ;(options()[0] as HTMLElement).click()

    await vi.waitFor(() => expect(router.currentRoute.value.params.pageId).toBe('Weekly meeting'))
    expect(dialog()).toBeNull()
  })

  it('does nothing on Enter when there is no result', async () => {
    const { router } = await shell(() => answer())
    await openByShortcut()
    await typeAndSearch('zebra')

    await press('Enter')

    expect(dialog()).not.toBeNull()
    expect(router.currentRoute.value.name).toBe('home')
  })

  it('closes with Escape', async () => {
    await shell(() => answer())
    await openByShortcut()

    document.body
      .querySelector('[role="dialog"]')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await flushPromises()

    expect(dialog()).toBeNull()
  })

  it('selects the last search when it opens again, so that typing replaces it', async () => {
    await shell(() => answer(meeting))
    await openByShortcut()
    await typeAndSearch('meeting')
    document.body
      .querySelector<HTMLButtonElement>('button[aria-label="Close quick switcher"]')!
      .click()
    await flushPromises()

    await openByShortcut()

    expect(input().value).toBe('meeting')
    expect(input().selectionStart).toBe(0)
    expect(input().selectionEnd).toBe('meeting'.length)
  })
})

describe('on a phone', () => {
  it('opens from the button in the header, and from the row in the drawer, which closes first', async () => {
    vi.stubGlobal('matchMedia', () => ({
      matches: false,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }))
    fakeTree({ pages, search: () => answer(meeting) })
    const mounted = await mountAt(RouterView, '/', 'en')
    wrapper = mounted.wrapper
    await flushPromises()
    await vi.waitFor(() => expect(wrapper!.find('main').text().length).toBeGreaterThan(0))

    await wrapper.get('[data-testid="search-open"]').trigger('click')
    await flushPromises()
    expect(dialog()).not.toBeNull()
    document.body
      .querySelector<HTMLButtonElement>('button[aria-label="Close quick switcher"]')!
      .click()
    await flushPromises()

    await wrapper.get('button[aria-label="Open the page list"]').trigger('click')
    await flushPromises()
    expect(drawer()).not.toBeNull()
    document.body.querySelector<HTMLButtonElement>('[data-testid="search-row"]')!.click()
    await flushPromises()

    expect(dialog()).not.toBeNull()
    expect(drawer()).toBeNull()
  })
})

describe('the shortcut', () => {
  it('opens the search once, however often it is pressed', async () => {
    const { server } = await shell(() => answer(meeting))
    await openByShortcut()
    await typeAndSearch('meeting')
    document.body
      .querySelector<HTMLButtonElement>('button[aria-label="Close quick switcher"]')!
      .click()
    await flushPromises()

    await openByShortcut()
    await openByShortcut()
    await openByShortcut()
    await vi.advanceTimersByTimeAsync(SEARCH_DEBOUNCE_MS * 2)
    await flushPromises()

    expect(server.searches()).toEqual(['meeting', 'meeting'])
  })

  it('closes the drawer first on a narrow screen, so that the two do not stack', async () => {
    vi.stubGlobal('matchMedia', () => ({
      matches: false,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }))
    fakeTree({ pages, search: () => answer() })
    const mounted = await mountAt(RouterView, '/', 'en')
    wrapper = mounted.wrapper
    await flushPromises()
    await vi.waitFor(() => expect(wrapper!.find('main').text().length).toBeGreaterThan(0))
    await wrapper.get('button[aria-label="Open the page list"]').trigger('click')
    await flushPromises()
    expect(drawer()).not.toBeNull()

    await openByShortcut()

    expect(drawer()).toBeNull()
    expect(dialog()).not.toBeNull()
    expect(document.body.querySelectorAll('[role="dialog"]')).toHaveLength(1)
  })

  it('leaves another open dialog alone, and opens nothing over it', async () => {
    await shell(() => answer())
    const other = document.createElement('div')
    other.setAttribute('role', 'dialog')
    document.body.appendChild(other)

    await openByShortcut()

    expect(dialog()).toBeNull()
  })
})
