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
import { MessageType } from './protocol'
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

describe('useKeepLostEdits', () => {
  it('keeps what was typed offline into a block deleted meanwhile, in a copy right after the page, and says so', async () => {
    const { sync, handle } = await typedOfflineIntoAReplacedPage()

    await reconnect()

    // The page itself is as the server has it: the typed text went with its block.
    expect(texts(handle.doc)).not.toContain('typed offline')
    const create = server.writes().find((write) => write.method === 'POST')!
    expect(create).toEqual({
      method: 'POST',
      path: '/api/workspaces/w1/pages',
      body: {
        title: 'Plan (your offline changes)',
        parentId: 'Work',
        afterId: 'Plan',
        beforeId: null,
      },
    })
    await vi.waitFor(() => expect(texts(hub.doc('new'))).toContain('old typed offline'))
    expect(useNotificationsStore().notices).toEqual([
      expect.objectContaining({ type: 'editsKept', pageId: 'new', title: 'Plan' }),
    ])
    expect(sync.lostEdits).toEqual([])
  })

  it('writes the copy from the first paragraph, so a client that opens it changes nothing', async () => {
    await typedOfflineIntoAReplacedPage()

    await reconnect()
    await vi.waitFor(() => expect(texts(hub.doc('new'))).toContain('typed offline'))

    const copy = hub.doc('new')
    const before = Y.encodeStateAsUpdate(copy)
    applyFirstParagraph(copy)
    expect(Y.encodeStateAsUpdate(copy)).toEqual(before)
  })

  it('titles the copy in the language of the page', async () => {
    await typedOfflineIntoAReplacedPage('nl')

    await reconnect()

    expect(server.writes().find((write) => write.method === 'POST')!.body.title).toBe(
      'Plan (je offline wijzigingen)',
    )
  })

  it('cuts a long title so that the title of the copy fits the server, in UTF-16 units and never half a character', async () => {
    const long = '😀'.repeat(300)
    await typedOfflineIntoAReplacedPage('en', long)

    await reconnect()

    const title = String(server.writes().find((write) => write.method === 'POST')!.body.title)
    expect(title.length).toBeLessThanOrEqual(500)
    expect(title.endsWith(' (your offline changes)')).toBe(true)
    expect(title).not.toMatch(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])/)
    expect(title.length).toBeGreaterThan(470)
  })

  /** Every POST answered with this, counted, instead of what the fake tree answers. */
  function refuseCreates(status: number, code: string) {
    const answer = server.fetch.getMockImplementation()!
    const refused: Record<string, unknown>[] = []
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method !== 'POST') return answer(input, init)
      refused.push(JSON.parse(String(init.body)) as Record<string, unknown>)
      return jsonResponse(status, { code })
    })
    return { refused, allow: () => server.fetch.mockImplementation(answer) }
  }

  const posts = () => server.writes().filter((write) => write.method === 'POST')

  it('forgets what waited to be kept when the person leaves, so nobody who signs in next gets it', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    server.failNext(503, 'network_error')
    await reconnect()
    expect(sync.lostEdits).toHaveLength(1)

    sync.destroy()

    expect(sync.lostEdits).toEqual([])
  })

  it('writes nothing when the person leaves while the copy is being made', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    const answer = server.fetch.getMockImplementation()!
    let release!: () => void
    const held = new Promise<void>((resolve) => (release = resolve))
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') await held
      return answer(input, init)
    })

    await reconnect()
    await vi.waitFor(() => expect(posts()).toHaveLength(0))
    sync.destroy()
    release()
    await flushPromises()

    expect(hub.sent(MessageType.Subscribe, 'new')).toEqual([])
    expect(useNotificationsStore().notices.map((notice) => notice.type)).not.toContain('editsKept')
  })

  it('puts the copy at the top level when the place beside the page is gone', async () => {
    await typedOfflineIntoAReplacedPage()
    server.failNext(409, 'parent_in_trash')

    await reconnect()

    await vi.waitFor(() => expect(posts()).toHaveLength(2))
    expect(posts()[1]!.body).toEqual({
      title: 'Plan (your offline changes)',
      parentId: null,
      afterId: null,
      beforeId: null,
    })
    await vi.waitFor(() => expect(texts(hub.doc('new'))).toContain('typed offline'))
  })

  it('says once that the copy is waiting, however often it is tried again', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    const { refused } = refuseCreates(500, 'unknown')

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

  it('waits for the workspace to be read before it makes the copy', async () => {
    await typedOfflineIntoAReplacedPage()
    useTreeStore().reset()

    await reconnect()
    expect(posts()).toEqual([])

    await useTreeStore().init()

    await vi.waitFor(() => expect(posts()).toHaveLength(1))
    await vi.waitFor(() => expect(texts(hub.doc('new'))).toContain('typed offline'))
  })

  it('tries again when the server could not be reached, once the connection is back', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    server.failNext(503, 'network_error')

    await reconnect()

    expect(sync.lostEdits).toHaveLength(1)
    expect(posts()).toHaveLength(1)
    expect(useNotificationsStore().notices.map((notice) => notice.type)).toEqual(['editsWaiting'])

    hub.current.serverClose(1006)
    await vi.waitFor(() => expect(posts()).toHaveLength(2), { timeout: 3000 })
    await vi.waitFor(() => expect(sync.lostEdits).toEqual([]))
    await vi.waitFor(() => expect(texts(hub.doc('new'))).toContain('typed offline'))
  })
})
