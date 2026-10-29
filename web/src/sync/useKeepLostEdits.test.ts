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
async function typedOfflineIntoAReplacedPage(locale: Locale = 'en') {
  server = fakeTree({ pages: outline(['Work', '  Plan']) })
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

  it('tries again when the copy could not be made, once the connection is back', async () => {
    const { sync } = await typedOfflineIntoAReplacedPage()
    server.failNext(503, 'unavailable')

    await reconnect()

    expect(sync.lostEdits).toHaveLength(1)
    expect(server.writes().filter((write) => write.method === 'POST')).toHaveLength(1)

    hub.current.serverClose(1006)
    await vi.waitFor(
      () => expect(server.writes().filter((w) => w.method === 'POST')).toHaveLength(2),
      {
        timeout: 3000,
      },
    )
    await vi.waitFor(() => expect(sync.lostEdits).toEqual([]))
    await vi.waitFor(() => expect(texts(hub.doc('new'))).toContain('typed offline'))
  })
})
