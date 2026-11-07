import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { RouterView } from 'vue-router'
import { SEARCH_DEBOUNCE_MS } from '../stores/search'
import { jsonResponse, mountAt } from '../testing/helpers'
import { registerLinkShortcut } from '../editor/linkShortcutRegistry'
import { fakeTree, outline } from '../testing/treeFixtures'
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

async function shell(
  search: NonNullable<Options>['search'],
  locale: 'en' | 'nl' = 'en',
  path = '/',
) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === '(min-width: 48rem)',
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }))
  const server = fakeTree({ pages, search })
  const mounted = await mountAt(RouterView, path, locale)
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
/** Which result is highlighted, as the listbox marks it. */
const active = () => options().findIndex((o) => o.hasAttribute('data-highlighted'))

async function openByShortcut(init: KeyboardEventInit = { ctrlKey: true }) {
  window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', bubbles: true, ...init }))
  await flushPromises()
}

async function type(text: string) {
  const field = input()
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
  it('opens with Ctrl+K and with Cmd+K, and with neither Alt nor Shift', async () => {
    await shell(() => answer())

    await openByShortcut({ altKey: true, ctrlKey: true })
    await openByShortcut({ shiftKey: true, ctrlKey: true })
    await openByShortcut({})
    expect(dialog()).toBeNull()

    await openByShortcut({ ctrlKey: true })
    expect(dialog()).not.toBeNull()
    document.body.querySelector<HTMLButtonElement>('button[aria-label="Close search"]')!.click()
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
    expect(input().getAttribute('placeholder')).toBe('Zoek in je pagina’s…')
    expect(document.body.textContent).toContain('Typ minstens 2 letters om te zoeken.')
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
    document.body.querySelector<HTMLButtonElement>('button[aria-label="Close search"]')!.click()
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
    document.body.querySelector<HTMLButtonElement>('button[aria-label="Close search"]')!.click()
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
    document.body.querySelector<HTMLButtonElement>('button[aria-label="Close search"]')!.click()
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
