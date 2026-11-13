import type { JSONContent } from '@tiptap/core'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { createAppI18n, type Locale } from '../i18n'
import { useTreeStore } from '../stores/tree'
import { jsonResponse, stubApi, type Route } from '../testing/helpers'
import HistoryPanel from './HistoryPanel.vue'
import type { HistoryState, VersionDetail, VersionList, VersionSummary } from './types'

const pageId = '0a1b2c3d-0000-4000-8000-000000000001'
const owner = { id: '0a1b2c3d-0000-4000-8000-0000000000aa', displayName: 'Jos' }
const listPath = `/api/pages/${pageId}/versions?limit=50`
const versionPath = (id: string) => `/api/pages/${pageId}/versions/${id}`

const newest: VersionSummary = {
  id: 'v3',
  createdAt: '2026-11-12T11:30:00Z',
  kind: 'rename',
  title: 'Plans, renamed',
  createdBy: owner,
  bytes: 120,
}
const middle: VersionSummary = {
  id: 'v2',
  createdAt: '2026-11-12T10:00:00Z',
  kind: 'automatic',
  title: 'Plans',
  createdBy: null,
  bytes: 110,
}
const oldest: VersionSummary = { ...middle, id: 'v1', createdAt: '2026-11-11T09:00:00Z' }

function list(
  versions: VersionSummary[],
  state: HistoryState = 'ready',
  next: string | null = null,
): VersionList {
  return {
    versions,
    next,
    state,
    failure: state === 'failed' ? 'unreadable' : null,
    pageContent: '12.4',
  }
}

function detailOf(version: VersionSummary, text: string, extra: JSONContent[] = []): VersionDetail {
  return {
    id: version.id,
    createdAt: version.createdAt,
    kind: version.kind,
    title: version.title,
    content: {
      type: 'doc',
      content: [{ type: 'paragraph', content: [{ type: 'text', text }] }, ...extra],
    },
  }
}

const get = (path: string, respond: Route['respond']): Route => ({ method: 'GET', path, respond })

let wrapper: VueWrapper | null = null

/** What the browser says about the screen: from `md` the list and the version are side by side. */
function stubScreen(wide: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('min-width') ? wide : false,
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }))
}

let i18n = createAppI18n('en')

async function open(
  routes: Route[],
  locale: Locale = 'en',
  extra: { canEdit?: boolean; inTrash?: boolean; pageVersion?: number } = {},
) {
  const fetchMock = stubApi(routes)
  const pinia = createPinia()
  setActivePinia(pinia)
  i18n = createAppI18n(locale)
  wrapper = mount(HistoryPanel, {
    props: { pageId, pageTitle: 'Plans, renamed', ...extra },
    global: { plugins: [pinia, i18n] },
    attachTo: document.body,
  })
  await flushPromises()
  return fetchMock
}

const panel = () => document.body.querySelector<HTMLElement>('[data-testid="history-panel"]')!
const rows = () => [
  ...document.body.querySelectorAll<HTMLButtonElement>('[data-testid="version-row"]'),
]
const preview = () => document.body.querySelector<HTMLElement>('[data-testid="version-preview"]')!
const text = (element: Element | null) => element?.textContent?.replace(/\s+/g, ' ').trim() ?? ''
const buttonNamed = (name: string) =>
  [...panel().querySelectorAll('button')].find((button) => text(button) === name)

const when = (iso: string, locale = 'en') =>
  new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso))

beforeEach(() => stubScreen(true))
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

describe('HistoryPanel', () => {
  it('is a dialog named after the page that lists the versions newest first: when, what kind, who and the title then', async () => {
    await open([get(listPath, () => jsonResponse(200, list([newest, middle])))])

    expect(panel().getAttribute('role')).toBe('dialog')
    expect(text(panel().querySelector('h2'))).toBe('History of “Plans, renamed”')
    // One line each, which a browser separates when it reads the row's name.
    expect(rows().map((row) => [...row.querySelectorAll('span')].map(text))).toEqual([
      [when(newest.createdAt), 'Renamed · Jos', 'Plans, renamed'],
      [when(middle.createdAt), 'Saved automatically', 'Plans'],
    ])
    // Nothing is chosen, so nothing is read yet.
    expect(text(preview())).toBe('Choose a version to see it.')
  })

  it('says it is loading, and says so to a screen reader', async () => {
    let answer: (response: Response) => void = () => {}
    stubApi([get(listPath, () => new Promise<Response>((resolve) => (answer = resolve)))])
    setActivePinia(createPinia())
    wrapper = mount(HistoryPanel, {
      props: { pageId, pageTitle: 'Plans' },
      global: { plugins: [createPinia(), createAppI18n('en')] },
      attachTo: document.body,
    })
    await nextTick()

    expect(text(panel().querySelector('[role="status"]'))).toBe('Loading the history…')
    answer(jsonResponse(200, list([middle])))
    await flushPromises()
    expect(rows()).toHaveLength(1)
  })

  it.each([
    ['pending', 'status', 'The latest changes are not in a version yet.'],
    ['failed', 'alert', 'could not be kept as a version'],
    ['full', 'status', 'The history of this workspace is full'],
    ['off', 'status', 'History is off for this workspace'],
  ] as const)('says when the history is %s', async (state, role, words) => {
    await open([get(listPath, () => jsonResponse(200, list([middle], state)))])

    const notice = panel().querySelector('[data-testid="history-state"]')!
    expect(notice.getAttribute('role')).toBe(role)
    expect(text(notice)).toContain(words)
    expect(rows()).toHaveLength(1)
  })

  it('says nothing about a history that is ready', async () => {
    await open([get(listPath, () => jsonResponse(200, list([middle])))])

    expect(panel().querySelector('[data-testid="history-state"]')).toBeNull()
  })

  it('has a word for a page without versions, also when history is off', async () => {
    await open([get(listPath, () => jsonResponse(200, list([])))])
    expect(text(panel().querySelector('[data-testid="history-empty"]'))).toBe(
      'No versions yet. A version is kept a few minutes after the page changes.',
    )
    wrapper?.unmount()
    document.body.innerHTML = ''

    await open([get(listPath, () => jsonResponse(200, list([], 'off')))])
    expect(text(panel().querySelector('[data-testid="history-empty"]'))).toBe(
      'No versions were kept.',
    )
  })

  it('says when the history could not be loaded, and loads it when asked again', async () => {
    let fail = true
    await open([
      get(listPath, () =>
        fail ? jsonResponse(500, { code: 'server_error' }) : jsonResponse(200, list([middle])),
      ),
    ])
    expect(text(panel().querySelector('[role="alert"]'))).toContain(
      'The history could not be loaded.',
    )

    fail = false
    buttonNamed('Try again')!.click()
    await flushPromises()
    expect(panel().querySelector('[role="alert"]')).toBeNull()
    expect(rows()).toHaveLength(1)
  })

  it('shows older versions when asked, and says when they could not be read', async () => {
    let fail = true
    await open([
      get(listPath, () => jsonResponse(200, list([newest], 'ready', 'cursor'))),
      get(`${listPath}&before=cursor`, () =>
        fail ? jsonResponse(500, {}) : jsonResponse(200, list([middle, oldest])),
      ),
    ])

    buttonNamed('Show older versions')!.click()
    await flushPromises()
    expect(text(panel().querySelector('[role="alert"]'))).toBe(
      'The older versions could not be loaded.',
    )
    expect(rows()).toHaveLength(1)

    fail = false
    buttonNamed('Show older versions')!.click()
    await flushPromises()
    expect(rows().map((row) => row.dataset.versionId)).toEqual(['v3', 'v2', 'v1'])
    expect(buttonNamed('Show older versions')).toBeUndefined()
  })

  it('shows a chosen version read-only, with its title then and its content, and reads it once', async () => {
    const fetchMock = await open([
      get(listPath, () => jsonResponse(200, list([newest, middle]))),
      get(versionPath('v2'), () => jsonResponse(200, detailOf(middle, 'What it said then'))),
      get(versionPath('v3'), () => jsonResponse(200, detailOf(newest, 'What it says now'))),
    ])

    rows()[1]!.click()
    await flushPromises()

    expect(rows()[1]!.getAttribute('aria-current')).toBe('true')
    expect(text(preview().querySelector('h3'))).toBe(
      `${when(middle.createdAt)} · Saved automatically`,
    )
    expect(text(preview().querySelector('[data-testid="version-title"]'))).toBe('Plans')
    const content = preview().querySelector<HTMLElement>('.ProseMirror')!
    expect(text(content)).toBe('What it said then')
    expect(content.getAttribute('contenteditable')).toBe('false')
    expect(content.getAttribute('aria-label')).toBe('The content of this version')

    // Another, and back: the first is not read again.
    rows()[0]!.click()
    await flushPromises()
    expect(text(preview().querySelector('.ProseMirror'))).toBe('What it says now')
    rows()[1]!.click()
    await flushPromises()
    expect(text(preview().querySelector('.ProseMirror'))).toBe('What it said then')
    const reads = fetchMock.mock.calls.filter(([input]) => String(input) === versionPath('v2'))
    expect(reads).toHaveLength(1)
  })

  it('shows a link of a version only with an address the editor allows, and a picture only from the app', async () => {
    const hostile = {
      ...detailOf(middle, 'Click', []),
      content: {
        type: 'doc',
        content: [
          {
            type: 'paragraph',
            content: [
              {
                type: 'text',
                text: 'bad',
                marks: [{ type: 'link', attrs: { href: 'javascript:alert(1)' } }],
              },
              {
                type: 'text',
                text: ' good',
                marks: [{ type: 'link', attrs: { href: 'https://example.com/' } }],
              },
            ],
          },
          { type: 'image', attrs: { attachmentId: 'https://evil.example/x.png', alt: 'outside' } },
        ],
      },
    }
    await open([
      get(listPath, () => jsonResponse(200, list([middle]))),
      get(versionPath('v2'), () => jsonResponse(200, hostile)),
    ])

    rows()[0]!.click()
    await flushPromises()

    const hrefs = [...preview().querySelectorAll('a')].map((a) => a.getAttribute('href'))
    expect(hrefs).not.toContain('javascript:alert(1)')
    expect(hrefs).toContain('https://example.com/')
    const image = preview().querySelector('img')!
    expect(image.getAttribute('src')).toBeNull()
  })

  it.each([
    [
      'history_unavailable',
      503,
      'This version cannot be shown right now. Try again in a moment.',
      true,
    ],
    [
      'version_unreadable',
      409,
      'This version cannot be shown: it holds something this app cannot display.',
      false,
    ],
    ['version_not_found', 404, 'This version is no longer kept.', false],
    ['server_error', 500, 'This version could not be loaded.', true],
  ] as const)(
    'says why a version cannot be shown (%s), and offers to try again only when that can help',
    async (code, status, message, retry) => {
      let fail = true
      await open([
        get(listPath, () => jsonResponse(200, list([middle]))),
        get(versionPath('v2'), () =>
          fail ? jsonResponse(status, { code }) : jsonResponse(200, detailOf(middle, 'Back again')),
        ),
      ])

      rows()[0]!.click()
      await flushPromises()

      expect(text(preview().querySelector('[role="alert"] p'))).toBe(message)
      const again = [...preview().querySelectorAll('button')].find((b) => text(b) === 'Try again')
      expect(again !== undefined).toBe(retry)
      if (again) {
        fail = false
        again.click()
        await flushPromises()
        expect(text(preview().querySelector('.ProseMirror'))).toBe('Back again')
      }
    },
  )

  it('on a phone shows the version in place of the list, with the focus on it, and goes back to the row it came from', async () => {
    stubScreen(false)
    // An id with a quote and a backslash: the row is still found again (the id goes into a selector).
    const odd = { ...middle, id: 'v"2\\' }
    await open([
      get(listPath, () => jsonResponse(200, list([newest, odd]))),
      get(versionPath(encodeURIComponent(odd.id)), () => jsonResponse(200, detailOf(odd, 'Then'))),
    ])
    const listSection = panel().querySelector('[data-testid="version-list"]')!.parentElement!
    expect(listSection.classList.contains('hidden')).toBe(false)
    expect(preview().classList.contains('hidden')).toBe(true)

    rows()[1]!.click()
    await flushPromises()

    expect(listSection.classList.contains('hidden')).toBe(true)
    expect(preview().classList.contains('hidden')).toBe(false)
    expect(document.activeElement).toBe(preview().querySelector('h3'))

    buttonNamed('Back to the versions')!.click()
    await flushPromises()
    expect(preview().classList.contains('hidden')).toBe(true)
    expect(document.activeElement).toBe(rows()[1])
  })

  it('takes a click on a link in a version, so the browser does not follow it', async () => {
    const linked = {
      ...detailOf(middle, ''),
      content: {
        type: 'doc',
        content: [
          {
            type: 'paragraph',
            content: [
              {
                type: 'text',
                text: 'elsewhere',
                marks: [{ type: 'link', attrs: { href: 'https://example.com/' } }],
              },
            ],
          },
        ],
      },
    }
    await open([
      get(listPath, () => jsonResponse(200, list([middle]))),
      get(versionPath('v2'), () => jsonResponse(200, linked)),
    ])
    rows()[0]!.click()
    await flushPromises()

    const link = preview().querySelector('a')!
    const click = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 })
    link.dispatchEvent(click)

    expect(click.defaultPrevented).toBe(true)
    // Nor with Ctrl, which on the page opens a new tab.
    const newTab = new MouseEvent('click', {
      bubbles: true,
      cancelable: true,
      button: 0,
      ctrlKey: true,
    })
    link.dispatchEvent(newTab)
    expect(newTab.defaultPrevented).toBe(true)
  })

  it('strikes a link through when the page it points to goes to the trash while the version is shown, and not when it comes back', async () => {
    const target = '0a1b2c3d-0000-4000-8000-0000000000cc'
    const linked = {
      ...detailOf(middle, ''),
      content: {
        type: 'doc',
        content: [
          {
            type: 'paragraph',
            content: [
              {
                type: 'text',
                text: 'the other page',
                marks: [{ type: 'link', attrs: { href: `/p/${target}` } }],
              },
            ],
          },
        ],
      },
    }
    await open([
      get(listPath, () => jsonResponse(200, list([middle]))),
      get(versionPath('v2'), () => jsonResponse(200, linked)),
    ])
    const tree = useTreeStore()
    const node = {
      id: target,
      parentId: null,
      title: 'Other',
      sortKey: 'a0',
      version: 1,
      updatedAt: '2026-11-12T10:00:00Z',
    }
    tree.$patch({ loaded: true, pages: [node], trash: [] })
    rows()[0]!.click()
    await flushPromises()
    const struck = () => preview().querySelector('[data-broken-link="trash"]') !== null

    expect(struck()).toBe(false)
    tree.$patch({ pages: [], trash: [{ ...node, deletedAt: '2026-11-12T11:00:00Z' }] })
    await flushPromises()
    expect(struck()).toBe(true)
    expect(preview().querySelector('[data-broken-link="trash"]')!.classList).toContain(
      'line-through',
    )

    tree.$patch({ pages: [node], trash: [] })
    await flushPromises()
    expect(struck()).toBe(false)
  })

  it('shows a version in the language the person switches to while it is open', async () => {
    await open([
      get(listPath, () => jsonResponse(200, list([middle]))),
      get(versionPath('v2'), () => jsonResponse(200, detailOf(middle, 'Then'))),
    ])
    rows()[0]!.click()
    await flushPromises()
    expect(preview().querySelector('.ProseMirror')!.getAttribute('aria-label')).toBe(
      'The content of this version',
    )

    i18n.global.locale.value = 'nl'
    await flushPromises()

    expect(preview().querySelector('.ProseMirror')!.getAttribute('aria-label')).toBe(
      'De inhoud van deze versie',
    )
    expect(text(preview().querySelector('.ProseMirror'))).toBe('Then')
  })

  describe('restoring', () => {
    const restorePath = `/api/pages/${pageId}/versions/v2/restore`
    const answer = { beforeVersionId: 'v4', restoreVersionId: 'v5', title: 'Plans', version: 8 }
    const choose = async (
      routes: Route[],
      extra: Parameters<typeof open>[2] = { canEdit: true, pageVersion: 7 },
    ) => {
      const fetchMock = await open(
        [
          get(listPath, () => jsonResponse(200, list([newest, middle]))),
          get(versionPath('v2'), () => jsonResponse(200, detailOf(middle, 'What it said then'))),
          ...routes,
        ],
        'en',
        extra,
      )
      rows()[1]!.click()
      await flushPromises()
      return fetchMock
    }
    const area = () => preview().querySelector<HTMLElement>('[data-testid="restore-area"]')

    it('asks first, says what it replaces and what it leaves, and restores the chosen version', async () => {
      const fetchMock = await choose([
        { method: 'POST', path: restorePath, respond: () => jsonResponse(200, answer) },
      ])

      buttonNamed('Restore this version')!.click()
      await flushPromises()
      const confirm = preview().querySelector<HTMLElement>('[data-testid="restore-confirm"]')!
      expect(document.activeElement).toBe(confirm.querySelector('h4'))
      expect(text(confirm)).toContain('The page’s content and title become this version’s.')
      expect(text(confirm)).toContain(
        'Its place in the tree, its tags, favorites and to-do settings stay',
      )
      // Asking changed nothing yet.
      expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)

      buttonNamed('Restore')!.click()
      await flushPromises()

      const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
      expect(JSON.parse(String(post[1]!.body))).toMatchObject({
        expectedContent: '12.4',
        expectedVersion: 7,
      })
      expect(wrapper!.emitted('restored')).toEqual([[answer]])
    })

    it('goes back to the button when the person thinks again', async () => {
      await choose([])
      buttonNamed('Restore this version')!.click()
      await flushPromises()

      buttonNamed('Cancel')!.click()
      await flushPromises()

      expect(area()!.querySelector('[data-testid="restore-confirm"]')).toBeNull()
      expect(document.activeElement).toBe(buttonNamed('Restore this version'))
    })

    it('sends one request at a time, and says it is busy', async () => {
      let finish: (response: Response) => void = () => {}
      const fetchMock = await choose([
        {
          method: 'POST',
          path: restorePath,
          respond: () => new Promise<Response>((resolve) => (finish = resolve)),
        },
      ])
      buttonNamed('Restore this version')!.click()
      await flushPromises()

      buttonNamed('Restore')!.click()
      await flushPromises()
      const busy = buttonNamed('Restoring…')!
      expect(busy.disabled).toBe(true)
      busy.click()
      await flushPromises()

      expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
      finish(jsonResponse(200, answer))
      await flushPromises()
      expect(wrapper!.emitted('restored')).toHaveLength(1)
    })

    it('says when the page changed, keeps the version on the screen, and reads the list again when asked', async () => {
      const fetchMock = await choose([
        {
          method: 'POST',
          path: restorePath,
          respond: () => jsonResponse(409, { code: 'page_changed' }),
        },
      ])
      buttonNamed('Restore this version')!.click()
      await flushPromises()
      buttonNamed('Restore')!.click()
      await flushPromises()

      expect(text(area()!.querySelector('[role="alert"]'))).toContain(
        'The page changed since the history was read.',
      )
      expect(wrapper!.emitted('restored')).toBeUndefined()
      const lists = () =>
        fetchMock.mock.calls.filter(([input]) => String(input) === listPath).length
      expect(lists()).toBe(1)

      buttonNamed('Refresh')!.click()
      await flushPromises()

      expect(lists()).toBe(2)
      expect(text(preview().querySelector('.ProseMirror'))).toBe('What it said then')
      expect(rows()[1]!.getAttribute('aria-current')).toBe('true')
      expect(area()!.querySelector('[role="alert"]')).toBeNull()
    })

    it.each([
      ['history_full', 'The history of this workspace is full'],
      ['attachment_missing', 'A picture or file in this version is no longer there'],
      ['content_too_large', 'The page would be too large to open'],
      ['forbidden', 'You may no longer edit this page.'],
      ['server_error', 'The version could not be restored. Nothing was changed.'],
    ])('says why a restore was refused (%s)', async (code, words) => {
      await choose([
        {
          method: 'POST',
          path: restorePath,
          respond: () => jsonResponse(code === 'server_error' ? 500 : 409, { code }),
        },
      ])
      buttonNamed('Restore this version')!.click()
      await flushPromises()
      buttonNamed('Restore')!.click()
      await flushPromises()

      expect(text(area()!.querySelector('[role="alert"]'))).toContain(words)
      expect(buttonNamed('Refresh')).toBeUndefined()
    })

    it('is not offered to someone who can only read', async () => {
      await choose([], { canEdit: false })

      expect(area()).toBeNull()
      expect(buttonNamed('Restore this version')).toBeUndefined()
    })

    it('says why it is not offered for a page in the trash, or with history off', async () => {
      await choose([], { canEdit: true, inTrash: true })
      expect(text(area())).toBe('The page is in the trash. Restore it from the trash first.')
      expect(buttonNamed('Restore this version')).toBeUndefined()
    })

    it('marks the version that is kept for a day', async () => {
      const kept = {
        ...middle,
        kind: 'before_restore' as const,
        protectedUntil: new Date(Date.now() + 3600_000).toISOString(),
      }
      const old = { ...oldest, protectedUntil: new Date(Date.now() - 3600_000).toISOString() }
      await open([get(listPath, () => jsonResponse(200, list([kept, old])))])

      expect(text(rows()[0]!)).toContain(`Kept at least until ${when(kept.protectedUntil)}`)
      expect(text(rows()[1]!)).not.toContain('Kept at least until')
    })
  })

  it('leaves the focus in the list when the version shows beside it', async () => {
    await open([
      get(listPath, () => jsonResponse(200, list([middle]))),
      get(versionPath('v2'), () => jsonResponse(200, detailOf(middle, 'Then'))),
    ])
    rows()[0]!.focus()

    rows()[0]!.click()
    await flushPromises()

    expect(document.activeElement).toBe(rows()[0])
  })

  it('asks to be closed by its close button', async () => {
    await open([get(listPath, () => jsonResponse(200, list([middle])))])

    panel().querySelector<HTMLButtonElement>('button[aria-label="Close the history"]')!.click()
    await flushPromises()

    expect(wrapper!.emitted('close')).toHaveLength(1)
  })

  it('speaks Dutch, dates included', async () => {
    await open([get(listPath, () => jsonResponse(200, list([newest], 'off')))], 'nl')

    expect(text(panel().querySelector('h2'))).toBe('Geschiedenis van “Plans, renamed”')
    expect([...rows()[0]!.querySelectorAll('span')].map(text)).toEqual([
      when(newest.createdAt, 'nl'),
      'Hernoemd · Jos',
      'Plans, renamed',
    ])
    expect(text(panel().querySelector('[data-testid="history-state"]'))).toContain(
      'De geschiedenis staat uit',
    )
  })
})
