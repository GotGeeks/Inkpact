import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { RouterView, createMemoryHistory, createRouter } from 'vue-router'
import { forgetImportLimit, type ImportRun, type ReportEntry } from '../imports/importApi'
import { useTreeStore } from '../stores/tree'
import { FakeXhr, stubXhr } from '../testing/fakeXhr'
import { jsonResponse, mountAt } from '../testing/helpers'
import { fakeTree } from '../testing/treeFixtures'
import { createAppI18n, type Locale } from '../i18n'
import ImportPage from './ImportPage.vue'

let wrapper: VueWrapper | null = null

beforeEach(() => {
  forgetImportLimit()
  stubXhr()
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

const counts = {
  notebooks: 3,
  notes: 40,
  todos: 12,
  tags: 5,
  images: 7,
  files: 2,
  alreadyImported: 0,
  alreadyInTrash: 0,
  trashed: 0,
  skipped: 0,
  failed: 0,
  unlisted: 0,
}

function importRun(overrides: Partial<ImportRun> = {}): ImportRun {
  return {
    id: 'imp1',
    workspaceId: 'w1',
    fileName: 'export.jex',
    state: 'completed',
    errorCode: null,
    totalItems: 50,
    processedItems: 50,
    createdAt: '2026-10-28T10:00:00Z',
    startedAt: '2026-10-28T10:00:01Z',
    finishedAt: '2026-10-28T10:01:00Z',
    counts,
    report: [],
    ...overrides,
  }
}

/** The tree API of the other tests, with the import's own calls added: what is read is whatever `current` holds. */
function server(options: { role?: 'owner' | 'editor' | 'viewer'; maxFileSize?: number } = {}) {
  const tree = fakeTree({ role: options.role })
  const treeFetch = tree.fetch.getMockImplementation()!
  const state = {
    current: importRun() as ImportRun | null,
    reads: 0,
    open: { open: false, id: null } as { open: boolean; id: string | null },
  }
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input)
    if (path === '/api/imports/limits')
      return jsonResponse(200, { maxFileSize: options.maxFileSize ?? 1_000_000 })
    if (path === '/api/workspaces/w1/imports/open') return jsonResponse(200, state.open)
    const read = /^\/api\/imports\/([^/]+)$/.exec(path)
    if (read) {
      state.reads++
      return state.current
        ? jsonResponse(200, state.current)
        : jsonResponse(404, { code: 'not_found' })
    }
    return treeFetch(input, init)
  })
  vi.stubGlobal('fetch', fetchMock)
  return { ...state, fetch: fetchMock, state }
}

async function open(path = '/import/joplin', locale: Locale = 'en') {
  const mounted = await mountAt(ImportPage, path, locale)
  wrapper = mounted.wrapper
  await useTreeStore().init()
  await flushPromises()
  return mounted
}

const text = () => wrapper!.text()

async function choose(file: File) {
  const input = wrapper!.get('[data-testid="import-file"]')
  Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
  await input.trigger('change')
  await flushPromises()
}

const jex = (size = 100) => new File(['x'.repeat(size)], 'export.jex')

describe('ImportPage before the upload', () => {
  it('says what to export from Joplin, and offers a file picker with the size limit', async () => {
    server({ maxFileSize: 2_000_000_000 })
    await open()

    expect(wrapper!.get('h1').text()).toBe('Import from Joplin')
    expect(text()).toContain('Export all')
    expect(text()).toContain('Importing the same file again adds nothing twice')
    expect(wrapper!.get('[data-testid="import-choose"]').text()).toBe('Choose a .jex file')
    expect(text()).toContain('Files up to 2 GB.')
    // No accept list: iOS greys out a .jex file, since it knows no type for the extension. The server checks the file.
    expect(wrapper!.get('[data-testid="import-file"]').attributes('accept')).toBeUndefined()
  })

  it('is in Dutch for someone who reads Dutch', async () => {
    server()
    await open('/import/joplin', 'nl')

    expect(wrapper!.get('h1').text()).toBe('Importeren uit Joplin')
    expect(text()).toContain('Bestand, Alles exporteren')
    expect(wrapper!.get('[data-testid="import-choose"]').text()).toBe('Kies een .jex-bestand')
  })

  it('offers no upload to someone who can only read the workspace', async () => {
    server({ role: 'viewer' })
    await open()

    expect(text()).toContain('you cannot import into it')
    expect(wrapper!.find('[data-testid="import-choose"]').exists()).toBe(false)
    expect(wrapper!.find('[data-testid="import-file"]').exists()).toBe(false)
  })

  it('refuses an empty file without sending it', async () => {
    server()
    await open()

    await choose(new File([], 'empty.jex'))

    expect(wrapper!.get('[data-testid="import-error"]').text()).toBe('That file is empty.')
    expect(FakeXhr.sent).toHaveLength(0)
  })

  it('says an import is already running before sending anything, with a link when it is your own', async () => {
    const api = server()
    api.state.open = { open: true, id: 'running-1' }
    const { router } = await open()

    await choose(jex())

    expect(wrapper!.get('[data-testid="import-error"]').text()).toBe(
      'An import is already running in this workspace. Wait until it has finished.',
    )
    expect(FakeXhr.sent).toHaveLength(0)
    const link = wrapper!.get('[data-testid="import-show-running"]')
    expect(link.text()).toBe('Show the import that is running')

    await link.trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.fullPath).toBe('/import/joplin/running-1')
  })

  it('gives no link to an import someone else started', async () => {
    const api = server()
    api.state.open = { open: true, id: null }
    await open()

    await choose(jex())

    expect(wrapper!.get('[data-testid="import-error"]').text()).toContain('already running')
    expect(wrapper!.find('[data-testid="import-show-running"]').exists()).toBe(false)
    expect(FakeXhr.sent).toHaveLength(0)
  })

  it('refuses a file over the limit without sending it', async () => {
    server({ maxFileSize: 50 })
    await open()

    await choose(jex(100))

    expect(wrapper!.get('[data-testid="import-error"]').text()).toBe(
      'That file is too large to import.',
    )
    expect(FakeXhr.sent).toHaveLength(0)
  })
})

describe('ImportPage while uploading', () => {
  async function startUpload() {
    const mounted = await open()
    await choose(jex(1000))
    await vi.waitFor(() => expect(FakeXhr.sent).toHaveLength(1))
    return { ...mounted, request: FakeXhr.sent[0]! }
  }

  it('sends the file to the workspace with its name, and shows how much has gone', async () => {
    server()
    const { request } = await startUpload()
    request.progress(250, 1000)
    await flushPromises()

    expect(request.url).toBe('/api/workspaces/w1/imports/joplin?name=export.jex')
    expect(wrapper!.get('[data-testid="import-uploading"]').text()).toContain(
      'Uploading export.jex',
    )
    expect(text()).toContain('250 bytes of 1 kB')
    const bar = wrapper!.get('progress')
    expect(bar.attributes('aria-label')).toBe('Upload progress')
    expect(bar.attributes('value')).toBe('250')
    expect(bar.attributes('max')).toBe('1000')
  })

  it('says it is checking the file once all of it is sent', async () => {
    server()
    const { request } = await startUpload()

    request.progress(1000, 1000)
    await flushPromises()

    expect(text()).toContain('Checking the file…')
  })

  it('goes to the import’s own address when the server has accepted the file, and follows it', async () => {
    const api = server()
    api.state.current = importRun({ state: 'running', processedItems: 5, report: null })
    const { router, request } = await startUpload()

    request.respond(202, { id: 'imp1' })
    await vi.waitFor(() =>
      expect(wrapper!.find('[data-testid="import-running"]').exists()).toBe(true),
    )

    expect(router.currentRoute.value.fullPath).toBe('/import/joplin/imp1')
    expect(wrapper!.get('[data-testid="import-running"]').text()).toContain('Importing export.jex')
    expect(text()).toContain('5 of 50 items')
  })

  it('stops the upload when it is cancelled, and is back at the start without an error', async () => {
    server()
    const { request } = await startUpload()

    await wrapper!
      .findAll('button')
      .find((b) => b.text() === 'Cancel')!
      .trigger('click')
    await flushPromises()

    expect(request.aborted).toBe(true)
    expect(wrapper!.find('[data-testid="import-choose"]').exists()).toBe(true)
    expect(wrapper!.find('[data-testid="import-error"]').exists()).toBe(false)
  })

  it.each([
    [
      409,
      'import_in_progress',
      'An import is already running in this workspace. Wait until it has finished.',
    ],
    [
      400,
      'import_invalid',
      'That is not a Joplin export. In Joplin desktop, choose File, Export all, JEX.',
    ],
    [413, 'import_too_large', 'That file is too large to import.'],
    [403, 'forbidden', 'You are not allowed to do that.'],
  ])('shows the reason when the server answers %s %s', async (status, code, message) => {
    server()
    const { request } = await startUpload()

    request.respond(status, { code })
    await flushPromises()

    expect(wrapper!.get('[data-testid="import-error"]').text()).toBe(message)
    expect(wrapper!.find('[data-testid="import-choose"]').exists()).toBe(true)
  })

  it('shows that the server could not be reached', async () => {
    server()
    const { request } = await startUpload()

    request.fail()
    await flushPromises()

    expect(wrapper!.get('[data-testid="import-error"]').text()).toContain('Could not reach')
  })

  it('names the import that started meanwhile when the connection drops, rather than the network', async () => {
    // The server refuses a second import without reading the body, which the browser sees as a dropped connection.
    const api = server()
    const { request } = await startUpload()
    api.state.open = { open: true, id: null }

    request.fail()
    await flushPromises()

    expect(wrapper!.get('[data-testid="import-error"]').text()).toContain('already running')
  })

  it('says how long to wait when the server says so', async () => {
    server()
    const { request } = await startUpload()

    request.respond(429, { code: 'too_many_requests' }, { 'Retry-After': '900' })
    await flushPromises()

    expect(wrapper!.get('[data-testid="import-error"]').text()).toBe(
      'Too many attempts from this device. Try again in 15 minutes.',
    )
  })

  it('asks the browser to keep the page while the upload runs, and not afterwards', async () => {
    server()
    const { request } = await startUpload()
    const leave = () => {
      const event = new Event('beforeunload', { cancelable: true })
      window.dispatchEvent(event)
      return event.defaultPrevented
    }

    expect(leave()).toBe(true)

    request.respond(400, { code: 'import_invalid' })
    await flushPromises()
    expect(leave()).toBe(false)
  })
})

describe('ImportPage while it imports', () => {
  it('shows progress that follows the server, every second', async () => {
    const api = server()
    api.state.current = importRun({ state: 'running', processedItems: 5, report: null })
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    await open('/import/joplin/imp1')

    expect(wrapper!.get('progress').attributes('value')).toBe('5')
    expect(text()).toContain('5 of 50 items')

    api.state.current = importRun({ state: 'running', processedItems: 30, report: null })
    await vi.advanceTimersByTimeAsync(1000)
    expect(wrapper!.get('progress').attributes('value')).toBe('30')
    expect(text()).toContain('30 of 50 items')
  })

  it('says it waits when the import is queued, with a bar that has no value', async () => {
    const api = server()
    api.state.current = importRun({
      state: 'queued',
      totalItems: 0,
      processedItems: 0,
      report: null,
    })
    await open('/import/joplin/imp1')

    expect(text()).toContain('Waiting for the import to start…')
    expect(wrapper!.get('progress').attributes('value')).toBeUndefined()
  })

  it('moves on to the report, and reads the page tree again, when the import ends', async () => {
    const api = server()
    api.state.current = importRun({ state: 'running', processedItems: 5, report: null })
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    await open('/import/joplin/imp1')
    const treeReads = () =>
      api.fetch.mock.calls.filter(([path]) => String(path) === '/api/workspaces/w1/pages').length
    const before = treeReads()

    api.state.current = importRun()
    await vi.advanceTimersByTimeAsync(1000)

    expect(wrapper!.get('[data-testid="import-result"]').text()).toBe('The import has finished')
    expect(treeReads()).toBe(before + 1)
  })

  it('says when the connection is lost before the import was ever read', async () => {
    const api = server()
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const original = api.fetch.getMockImplementation()!
    api.fetch.mockImplementation(async (input, init) => {
      if (String(input) === '/api/imports/imp1') throw new TypeError('Failed to fetch')
      return original(input, init)
    })
    await open('/import/joplin/imp1')
    expect(text()).toContain('Loading the import…')

    await vi.advanceTimersByTimeAsync(3000)

    expect(text()).toContain('Lost contact with the server. Trying again…')
  })

  it('says when the connection is lost, and goes on once it is back', async () => {
    const api = server()
    api.state.current = importRun({ state: 'running', processedItems: 5, report: null })
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    await open('/import/joplin/imp1')
    const original = api.fetch.getMockImplementation()!
    api.fetch.mockImplementation(async (input, init) => {
      if (String(input).startsWith('/api/imports/imp')) throw new TypeError('Failed to fetch')
      return original(input, init)
    })

    await vi.advanceTimersByTimeAsync(3000)
    expect(text()).toContain('Lost contact with the server. Trying again…')

    api.fetch.mockImplementation(original)
    await vi.advanceTimersByTimeAsync(1000)
    expect(text()).not.toContain('Lost contact')
  })
})

describe('ImportPage report', () => {
  const entries: ReportEntry[] = [
    {
      kind: 'note',
      externalId: 'a'.repeat(32),
      title: 'Web clipping',
      status: 'imperfect',
      pageId: 'page-1',
      reasons: [
        { code: 'removed_element', detail: 'svg', count: 120 },
        { code: 'link_removed', detail: 'relative', count: 8 },
      ],
    },
    {
      kind: 'note',
      externalId: 'b'.repeat(32),
      title: 'Secret plans',
      status: 'skipped',
      reasons: [{ code: 'encrypted', count: 1 }],
    },
    {
      kind: 'note',
      externalId: 'c'.repeat(32),
      title: 'Meeting (conflict)',
      status: 'conflict',
      pageId: 'page-3',
      reasons: [],
    },
    {
      kind: 'notebook',
      externalId: 'd'.repeat(32),
      title: '',
      status: 'failed',
      reasons: [{ code: 'something_from_the_future', count: 1 }],
    },
  ]

  async function openReport(overrides: Partial<ImportRun> = {}, locale: Locale = 'en') {
    const api = server()
    api.state.current = importRun({ report: entries, ...overrides })
    const { router } = await open('/import/joplin/imp1', locale)
    return { ...api, router }
  }

  it('counts what was imported, per kind', async () => {
    await openReport()

    const count = (key: string) => wrapper!.get(`[data-testid="import-count-${key}"]`).text()
    expect(count('notebooks')).toBe('Notebooks3')
    expect(count('notes')).toBe('Notes40')
    expect(count('todos')).toBe('Of which to-dos12')
    expect(count('tags')).toBe('Tags5')
    expect(count('images')).toBe('Images7')
    expect(count('files')).toBe('Files2')
  })

  it('lists every item that was skipped or came across imperfectly, with its status, kind and reasons', async () => {
    await openReport()

    const rows = wrapper!.findAll('[data-testid="import-report"] > li')
    expect(rows.map((row) => row.attributes('data-status'))).toEqual([
      'imperfect',
      'skipped',
      'conflict',
      'failed',
    ])
    expect(wrapper!.get('[data-testid="import-result"]').text()).toBe(
      'The import has finished, with some things to look at',
    )
    expect(text()).toContain('4 items were skipped or did not come across exactly as they were.')

    expect(rows[0]!.text()).toContain('Imported, with changes')
    expect(rows[0]!.text()).toContain(
      'The “svg” element was removed, with what was inside it. (120 times)',
    )
    expect(rows[0]!.text()).toContain(
      'A link with a relative address was removed. Its text was kept. (8 times)',
    )
    expect(rows[1]!.text()).toContain('Skipped')
    expect(rows[1]!.text()).toContain('It is encrypted in the export, so it was skipped.')
    expect(rows[2]!.text()).toContain('Joplin conflict')
    expect(rows[3]!.text()).toContain('Notebook')
    expect(rows[3]!.text()).toContain('Untitled')
    expect(rows[3]!.text()).toContain('Something did not come across as it was.')
  })

  it('links to each page that was made, and to no page for an item that was skipped', async () => {
    await openReport()

    const links = wrapper!.findAll('[data-testid="import-report"] a')
    expect(links.map((a) => [a.text(), a.attributes('href')])).toEqual([
      ['Web clipping', '/p/page-1'],
      ['Meeting (conflict)', '/p/page-3'],
    ])
  })

  it('says so when everything came across as it was', async () => {
    await openReport({ report: [] })

    expect(wrapper!.get('[data-testid="import-result"]').text()).toBe('The import has finished')
    expect(text()).toContain('Everything came across as it was.')
    expect(wrapper!.find('[data-testid="import-report"]').exists()).toBe(false)
  })

  it('lists only what was left alone, in the right number form', async () => {
    await openReport({
      counts: {
        ...counts,
        alreadyImported: 1,
        alreadyInTrash: 0,
        trashed: 2,
        skipped: 1,
        unlisted: 1500,
      },
    })

    const lines = wrapper!
      .get('[data-testid="import-left"]')
      .findAll('li')
      .map((li) => li.text())
    expect(lines).toEqual([
      '1 item was already imported earlier and was left alone.',
      '2 notes were in Joplin’s trash and were not imported.',
      '1 item was skipped.',
      '1,500 more lines are not listed below.',
    ])
  })

  it('shows the first hundred lines, and the rest on request', async () => {
    const many: ReportEntry[] = Array.from({ length: 250 }, (_, i) => ({
      kind: 'note',
      externalId: String(i).padStart(32, '0'),
      title: `Note ${i}`,
      status: 'imperfect',
      reasons: [{ code: 'math', count: 1 }],
    }))
    await openReport({ report: many })
    const rows = () => wrapper!.findAll('[data-testid="import-report"] > li')

    expect(rows()).toHaveLength(100)
    const more = wrapper!.findAll('button').find((b) => b.text().startsWith('Show'))!
    expect(more.text()).toBe('Show 100 more')

    await more.trigger('click')
    expect(rows()).toHaveLength(200)
    const last = wrapper!.findAll('button').find((b) => b.text().startsWith('Show'))!
    expect(last.text()).toBe('Show 50 more')
    await wrapper!
      .findAll('button')
      .find((b) => b.text().startsWith('Show'))!
      .trigger('click')
    expect(rows()).toHaveLength(250)
    expect(wrapper!.findAll('button').some((b) => b.text().startsWith('Show'))).toBe(false)
  })

  it('is in Dutch for someone who reads Dutch', async () => {
    await openReport({}, 'nl')

    expect(wrapper!.get('[data-testid="import-result"]').text()).toBe(
      'De import is klaar, met een paar dingen om te bekijken',
    )
    expect(text()).toContain('Het element “svg” is verwijderd, met wat erin stond. (120 keer)')
    expect(wrapper!.get('[data-testid="import-count-notebooks"]').text()).toBe('Notitieboeken3')
  })

  it('says why a failed import stopped, and that what was imported is kept', async () => {
    await openReport({ state: 'failed', errorCode: 'sidecar_unavailable' })

    expect(wrapper!.get('[data-testid="import-result"]').text()).toBe('The import stopped')
    const failure = wrapper!.get('[data-testid="import-failure"]')
    expect(failure.attributes('role')).toBe('alert')
    expect(failure.text()).toContain(
      'The part of the server that builds the pages is not available.',
    )
    expect(failure.text()).toContain('What was imported before it stopped is kept.')
  })

  it('has a way to import another file, which goes back to the start', async () => {
    const { router } = await openReport()

    await wrapper!
      .findAll('button')
      .find((b) => b.text() === 'Import another file')!
      .trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.fullPath).toBe('/import/joplin')
    expect(wrapper!.find('[data-testid="import-choose"]').exists()).toBe(true)
  })

  it('does not offer another import to someone who can only read', async () => {
    await openReport()
    // The report of an earlier import stays readable after the role has changed.
    const tree = useTreeStore()
    tree.workspace = { ...tree.workspace!, role: 'viewer' }
    await flushPromises()

    expect(wrapper!.findAll('button').some((b) => b.text() === 'Import another file')).toBe(false)
  })
})

describe('ImportPage when leaving during an upload', () => {
  /** The page inside a RouterView, as the app shows it, so that its leave guard is in place. */
  async function openInRouter() {
    server()
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { render: () => null } },
        { path: '/import/joplin/:importId?', name: 'import', component: ImportPage },
      ],
    })
    await router.push('/import/joplin')
    await router.isReady()
    wrapper = mount(defineComponent({ render: () => h(RouterView) }), {
      global: { plugins: [pinia, router, createAppI18n('en')] },
    })
    await useTreeStore().init()
    await flushPromises()
    await choose(jex(1000))
    await vi.waitFor(() => expect(FakeXhr.sent).toHaveLength(1))
    return { router, request: FakeXhr.sent[0]! }
  }

  it('asks first, and stays when the answer is no', async () => {
    const confirm = vi.fn(() => false)
    vi.stubGlobal('confirm', confirm)
    const { router, request } = await openInRouter()

    await router.push('/')

    expect(confirm).toHaveBeenCalledWith('The upload is not finished. Leave this page and stop it?')
    expect(router.currentRoute.value.name).toBe('import')
    expect(request.aborted).toBe(false)
  })

  it('stops the upload when the answer is yes', async () => {
    vi.stubGlobal(
      'confirm',
      vi.fn(() => true),
    )
    const { router, request } = await openInRouter()

    await router.push('/')
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('home')
    expect(request.aborted).toBe(true)
  })

  it('does not ask once the upload is done', async () => {
    const confirm = vi.fn(() => false)
    vi.stubGlobal('confirm', confirm)
    const { router, request } = await openInRouter()
    request.respond(202, { id: 'imp1' })
    await vi.waitFor(() => expect(router.currentRoute.value.fullPath).toBe('/import/joplin/imp1'))

    await router.push('/')

    expect(confirm).not.toHaveBeenCalled()
    expect(router.currentRoute.value.name).toBe('home')
  })
})

describe('ImportPage when the import cannot be shown', () => {
  it('says it was not found, and offers to start again', async () => {
    const api = server()
    api.state.current = null
    const { router } = await open('/import/joplin/gone')

    expect(wrapper!.get('[data-testid="import-unavailable"]').text()).toBe(
      'That could not be found.',
    )

    await wrapper!.get('button').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.fullPath).toBe('/import/joplin')
  })
})
