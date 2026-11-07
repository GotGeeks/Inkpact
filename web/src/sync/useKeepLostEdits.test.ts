import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import * as Y from 'yjs'
import { applyFirstParagraph, FIELD } from '../editor/firstParagraph'
import { createAppI18n, type Locale } from '../i18n'
import { useNotificationsStore } from '../stores/notifications'
import { useSyncStore } from '../stores/sync'
import { useTreeStore } from '../stores/tree'
import { FakeSyncHub } from '../testing/fakeSyncHub'
import { jsonResponse } from '../testing/helpers'
import { fakeTree, outline, type FakeTree } from '../testing/treeFixtures'
import { useKeepLostEdits } from './useKeepLostEdits'

let hub: FakeSyncHub
let server: FakeTree
let wrapper: VueWrapper | null = null

beforeEach(() => {
  hub = new FakeSyncHub()
  vi.stubGlobal('WebSocket', function () {
    return hub.createSocket()
  })
})
afterEach(() => {
  useSyncStore().destroy()
  wrapper?.unmount()
  wrapper = null
  vi.unstubAllGlobals()
})

function paragraph(text: string): Y.XmlElement {
  const element = new Y.XmlElement('paragraph')
  const content = new Y.XmlText()
  content.insert(0, text)
  element.insert(0, [content])
  return element
}

function editOf(doc: Y.Doc, change: (fragment: Y.XmlFragment) => void): Uint8Array {
  const before = Y.encodeStateVector(doc)
  doc.transact(() => change(doc.getXmlFragment(FIELD)))
  return Y.encodeStateAsUpdate(doc, before)
}

const texts = (doc: Y.Doc) =>
  doc
    .getXmlFragment(FIELD)
    .toArray()
    .map((node) => node.toString())
    .join('')

/**
 * The app with the page "Plan" open, its connection lost while the person typed into its paragraph, and an import that
 * replaced the page meanwhile; then the connection comes back.
 */
async function typedOfflineIntoAReplacedPage(locale: Locale = 'en', title = 'Plan') {
  server = fakeTree({
    pages: outline(['Work', '  Plan']).map((page) =>
      page.id === 'Plan' ? { ...page, title } : page,
    ),
  })
  const pinia = createPinia()
  setActivePinia(pinia)
  await useTreeStore().init()
  wrapper = mount(
    defineComponent({
      setup() {
        useKeepLostEdits()
        return () => null
      },
    }),
    { global: { plugins: [pinia, createAppI18n(locale)] } },
  )

  const author = new Y.Doc()
  applyFirstParagraph(author)
  hub.store('Plan', Y.encodeStateAsUpdate(author))
  hub.store(
    'Plan',
    editOf(author, (fragment) => fragment.insert(1, [paragraph('old')])),
  )
  const sync = useSyncStore()
  const handle = sync.open('Plan', { init: applyFirstParagraph })
  await flushPromises()

  hub.accepting = false
  hub.current.serverClose(1006)
  handle.doc.transact(() => {
    const block = handle.doc.getXmlFragment(FIELD).get(1) as Y.XmlElement
    ;(block.get(0) as Y.XmlText).insert(3, ' typed offline')
  })
  hub.store(
    'Plan',
    editOf(author, (fragment) => {
      fragment.delete(0, fragment.length)
      fragment.insert(0, [paragraph('from Joplin')])
    }),
  )
  return { sync, handle }
}

/** The network is back: the hub accepts again, and the browser says so. */
async function reconnect() {
  hub.accepting = true
  window.dispatchEvent(new Event('online'))
  await vi.waitFor(() => expect(useSyncStore().display).toBe('live'), { timeout: 3000 })
  await flushPromises()
}

const posts = () => server.writes().filter((write) => write.method === 'POST')

/** The page a create asked for, as the document its content makes. */
function contentOf(write: { body: Record<string, unknown> }): Y.Doc {
  const doc = new Y.Doc()
  Y.applyUpdate(
    doc,
    Uint8Array.from(atob(String(write.body.content)), (c) => c.charCodeAt(0)),
  )
  return doc
}

/** Every POST answered with this, counted, instead of what the fake tree answers. */
function refuseCreates(status: number, code: string) {
  const answer = server.fetch.getMockImplementation()!
  const refused: Record<string, unknown>[] = []
  server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method !== 'POST') return answer(input, init)
    refused.push(JSON.parse(String(init.body)) as Record<string, unknown>)
    return jsonResponse(status, { code })
  })
  return refused
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/

describe('useKeepLostEdits', () => {
  it('keeps what was typed offline into a block deleted meanwhile, in a copy right after the page, and says so', async () => {
    const { sync, handle } = await typedOfflineIntoAReplacedPage()

    await reconnect()

    // The page itself is as the server has it: the typed text went with its block.
    expect(texts(handle.doc)).not.toContain('typed offline')
    await vi.waitFor(() => expect(posts()).toHaveLength(1))
    const create = posts()[0]!
    expect(create.path).toBe('/api/workspaces/w1/pages')
    expect(create.body).toEqual({
      id: expect.stringMatching(uuid),
      title: 'Plan (your offline changes)',
      parentId: 'Work',
      afterId: 'Plan',
      beforeId: null,
      content: expect.any(String),
    })
    // The page and what was typed come in the one request: there is never a page without its content.
    expect(texts(contentOf(create))).toContain('old typed offline')
    await vi.waitFor(() =>
      expect(useNotificationsStore().notices).toEqual([
        expect.objectContaining({ type: 'editsKept', pageId: create.body.id, title: 'Plan' }),
      ]),
    )
    expect(sync.lostEdits).toEqual([])
  })

  it('writes the copy from the first paragraph, so a client that opens it changes nothing', async () => {
    await typedOfflineIntoAReplacedPage()

    await reconnect()
    await vi.waitFor(() => expect(posts()).toHaveLength(1))

    const copy = contentOf(posts()[0]!)
    const before = Y.encodeStateAsUpdate(copy)
    applyFirstParagraph(copy)
    expect(Y.encodeStateAsUpdate(copy)).toEqual(before)
  })

  it('titles the copy in the language of the page', async () => {
    await typedOfflineIntoAReplacedPage('nl')

    await reconnect()

    await vi.waitFor(() => expect(posts()).toHaveLength(1))
    expect(posts()[0]!.body.title).toBe('Plan (je offline wijzigingen)')
  })

  it('cuts a long title so that the title of the copy fits the server, in UTF-16 units and never half a character', async () => {
    await typedOfflineIntoAReplacedPage('en', '😀'.repeat(300))

    await reconnect()

    await vi.waitFor(() => expect(posts()).toHaveLength(1))
    const title = String(posts()[0]!.body.title)
    expect(title.length).toBeLessThanOrEqual(500)
    expect(title.endsWith(' (your offline changes)')).toBe(true)
    expect(title).not.toMatch(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])/)
    expect(title.length).toBeGreaterThan(470)
  })

  it('asks for the same page again when the answer to the first ask was lost, so there is never a second copy', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    const answer = server.fetch.getMockImplementation()!
    let lost = false
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const response = await answer(input, init)
      if (init?.method === 'POST' && !lost) {
        // The server made the page; its answer never arrived.
        lost = true
        throw new TypeError('Failed to fetch')
      }
      return response
    })

    await reconnect()
    await vi.waitFor(() => expect(posts()).toHaveLength(1))
    hub.current.serverClose(1006)
    await vi.waitFor(() => expect(posts()).toHaveLength(2), { timeout: 3000 })

    expect(posts()[1]!.body.id).toBe(posts()[0]!.body.id)
    await vi.waitFor(() => expect(sync.lostEdits).toEqual([]))
  })

  it('makes the copy in the workspace of the page, whatever workspace is on screen', async () => {
    await typedOfflineIntoAReplacedPage()
    const answer = server.fetch.getMockImplementation()!
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input) === '/api/pages/Plan' && (init?.method ?? 'GET') === 'GET') {
        const detail = (await (await answer(input, init)).json()) as Record<string, unknown>
        return jsonResponse(200, { ...detail, workspaceId: 'w2' })
      }
      return answer(input, init)
    })

    await reconnect()

    await vi.waitFor(() => expect(posts()).toHaveLength(1))
    expect(posts()[0]!.path).toBe('/api/workspaces/w2/pages')
  })

  it('forgets what waited to be kept when the person leaves, so nobody who signs in next gets it', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    server.failNext(503, 'network_error')
    await reconnect()
    await vi.waitFor(() => expect(posts()).toHaveLength(1))
    expect(sync.lostEdits).toHaveLength(1)

    sync.destroy()

    expect(sync.lostEdits).toEqual([])
  })

  it('tells nobody when the person left while the copy was being made', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    const answer = server.fetch.getMockImplementation()!
    let release!: () => void
    const held = new Promise<void>((resolve) => (release = resolve))
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') await held
      return answer(input, init)
    })

    await reconnect()
    sync.destroy()
    release()
    await flushPromises()

    expect(useNotificationsStore().notices.map((notice) => notice.type)).not.toContain('editsKept')
  })

  it('puts the copy at the top level, with the same id, when the place beside the page is gone', async () => {
    await typedOfflineIntoAReplacedPage()
    server.failNext(409, 'parent_in_trash')

    await reconnect()

    await vi.waitFor(() => expect(posts()).toHaveLength(2))
    expect(posts()[1]!.body).toEqual(expect.objectContaining({ parentId: null, afterId: null }))
    expect(posts()[1]!.body.id).toBe(posts()[0]!.body.id)
  })

  it('says once that the copy is waiting, however often it is tried again', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    const refused = refuseCreates(500, 'unknown')

    await reconnect()
    await vi.waitFor(() => expect(refused).toHaveLength(2))
    hub.current.serverClose(1006)
    await vi.waitFor(() => expect(refused).toHaveLength(4), { timeout: 3000 })
    await flushPromises()

    expect(useNotificationsStore().notices).toEqual([
      expect.objectContaining({ type: 'editsWaiting', title: 'Plan' }),
    ])
    expect(sync.lostEdits).toHaveLength(1)
  })

  it('says once that the copy is too large when the server refuses it for its size, and does not try it again', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    const refused = refuseCreates(413, 'content_too_large')

    await reconnect()
    await vi.waitFor(() => expect(refused).toHaveLength(1))
    hub.current.serverClose(1006)
    await vi.waitFor(() => expect(useSyncStore().display).toBe('live'), { timeout: 3000 })
    await flushPromises()

    // Not at the top level either (a copy too large at one place is too large everywhere), and not after reconnecting:
    // it would send megabytes for the same answer.
    expect(refused).toHaveLength(1)
    expect(useNotificationsStore().notices).toEqual([
      expect.objectContaining({ type: 'editsTooLarge', title: 'Plan' }),
    ])
    // The edits still wait in the tab; they go when the person signs out, as every waiting copy.
    expect(sync.lostEdits).toHaveLength(1)
  })

  it('takes a 413 of a proxy in front of the server for a copy that is waiting, not one that is too large', async () => {
    await typedOfflineIntoAReplacedPage()
    const answer = server.fetch.getMockImplementation()!
    const refused: Record<string, unknown>[] = []
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method !== 'POST') return answer(input, init)
      refused.push(JSON.parse(String(init.body)) as Record<string, unknown>)
      return new Response('<html><body>413 Request Entity Too Large</body></html>', {
        status: 413,
        headers: { 'Content-Type': 'text/html' },
      })
    })

    await reconnect()
    await vi.waitFor(() => expect(refused).toHaveLength(1))
    await flushPromises()

    expect(useNotificationsStore().notices.map((notice) => notice.type)).toEqual(['editsWaiting'])
    // Refused for its size all the same: not sent again to the top level.
    expect(refused).toHaveLength(1)
    hub.current.serverClose(1006)
    await vi.waitFor(() => expect(refused).toHaveLength(2), { timeout: 3000 })
  })

  it('says that the copy is too large after having said it was waiting, and sends the same content each time', async () => {
    await typedOfflineIntoAReplacedPage()
    const failed = refuseCreates(500, 'server_error')
    await reconnect()
    await vi.waitFor(() => expect(failed).toHaveLength(2))
    await flushPromises()

    const refused = refuseCreates(413, 'content_too_large')
    hub.current.serverClose(1006)
    await vi.waitFor(() => expect(refused).toHaveLength(1), { timeout: 3000 })
    await flushPromises()

    expect(useNotificationsStore().notices.map((notice) => notice.type)).toEqual([
      'editsWaiting',
      'editsTooLarge',
    ])
    expect(refused[0]!.content).toBe(failed[0]!.content)
    expect(refused[0]!.id).toBe(failed[0]!.id)
  })

  it('tries again when the server could not be reached, once the connection is back', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    server.failNext(503, 'network_error')

    await reconnect()

    await vi.waitFor(() => expect(posts()).toHaveLength(1))
    expect(sync.lostEdits).toHaveLength(1)
    expect(useNotificationsStore().notices.map((notice) => notice.type)).toEqual(['editsWaiting'])

    hub.current.serverClose(1006)
    await vi.waitFor(() => expect(posts()).toHaveLength(2), { timeout: 3000 })
    await vi.waitFor(() => expect(sync.lostEdits).toEqual([]))
    expect(texts(contentOf(posts()[1]!))).toContain('typed offline')
  })
})
