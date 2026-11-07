import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { jsonResponse, mountAt } from '../testing/helpers'
import { fakeTree, outline } from '../testing/treeFixtures'
import { useTreeStore } from '../stores/tree'
import BacklinksPanel from './BacklinksPanel.vue'

let wrapper: VueWrapper | null = null

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

const Host = (pageId: string) => defineComponent({ render: () => h(BacklinksPanel, { pageId }) })

async function open(options: Parameters<typeof fakeTree>[0] = {}, locale: 'en' | 'nl' = 'en') {
  const server = fakeTree({ pages: outline(['Target', 'Source', 'Other']), ...options })
  const mounted = await mountAt(Host('Target'), '/', locale)
  wrapper = mounted.wrapper
  return { server, ...mounted }
}

const entries = () => wrapper!.findAll('li').map((li) => li.text())

describe('the backlinks of a page', () => {
  it('lists the pages that link to it under a heading that counts them, each a link to that page', async () => {
    await open({
      backlinks: {
        Target: {
          items: [
            { id: 'Source', title: 'Source', parentTitle: null },
            { id: 'Other', title: 'Other', parentTitle: 'Notebook' },
          ],
          more: false,
        },
      },
    })

    expect(wrapper!.get('h2').text()).toBe('Linked from 2 pages')
    expect(entries()).toEqual(['Source', 'OtherNotebook'])
    expect(wrapper!.findAll('a').map((a) => a.attributes('href'))).toEqual([
      '/p/Source',
      '/p/Other',
    ])
    expect(wrapper!.get('nav').attributes('aria-labelledby')).toBe(
      wrapper!.get('h2').attributes('id'),
    )
  })

  it('says one page in the singular, and Dutch words in Dutch', async () => {
    await open(
      {
        backlinks: {
          Target: { items: [{ id: 'Source', title: 'Source', parentTitle: null }], more: false },
        },
      },
      'nl',
    )

    expect(wrapper!.get('h2').text()).toBe('Gelinkt vanaf 1 pagina')
  })

  it('names a page without a title "Untitled"', async () => {
    await open({
      backlinks: {
        Target: { items: [{ id: 'Source', title: '', parentTitle: null }], more: false },
      },
    })

    expect(entries()).toEqual(['Untitled'])
  })

  it('says when there are more than are shown', async () => {
    await open({
      backlinks: {
        Target: { items: [{ id: 'Source', title: 'Source', parentTitle: null }], more: true },
      },
    })

    expect(wrapper!.text()).toContain('There are more pages that link here than are shown.')
  })

  it('shows nothing at all for a page nothing links to', async () => {
    await open()

    expect(wrapper!.find('nav').exists()).toBe(false)
    expect(wrapper!.text()).toBe('')
  })

  it('shows nothing, and no error, when the server cannot say', async () => {
    await open({ backlinks: { Target: jsonResponse(500, { code: 'server_error' }) as never } })

    expect(wrapper!.find('nav').exists()).toBe(false)
  })

  it('is asked again when the tab comes back, so a link typed since is found', async () => {
    const { server } = await open()
    expect(server.backlinkReads()).toEqual(['Target'])
    server.setBacklinks('Target', {
      items: [{ id: 'Source', title: 'Source', parentTitle: null }],
      more: false,
    })

    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true })
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()

    expect(server.backlinkReads()).toEqual(['Target', 'Target'])
    expect(entries()).toEqual(['Source'])
  })

  it('is not asked while the tab is hidden', async () => {
    const { server } = await open()

    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true })
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()

    expect(server.backlinkReads()).toEqual(['Target'])
  })

  it('is asked again when the tree changed, which is how a page that links here being trashed or restored shows', async () => {
    const { server } = await open({
      backlinks: {
        Target: { items: [{ id: 'Source', title: 'Source', parentTitle: null }], more: false },
      },
    })
    expect(entries()).toEqual(['Source'])
    server.setBacklinks('Target', { items: [], more: false })

    const tree = useTreeStore()
    tree.treeVersion += 1
    await flushPromises()

    expect(wrapper!.find('nav').exists()).toBe(false)
  })

  it('takes the answer to the last ask, not one that comes in late', async () => {
    let release: (answer: Response) => void = () => {}
    const slow = new Promise<Response>((resolve) => (release = resolve))
    const { server } = await open()
    const answer = server.fetch.getMockImplementation()!
    let first = true
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).endsWith('/backlinks') && first) {
        first = false
        return slow
      }
      return answer(input, init)
    })
    server.setBacklinks('Target', {
      items: [{ id: 'Other', title: 'Fresh', parentTitle: null }],
      more: false,
    })

    const tree = useTreeStore()
    tree.treeVersion += 1 // The first ask, held back.
    await nextTick()
    tree.treeVersion += 1 // The second.
    await flushPromises()
    release(
      jsonResponse(200, {
        items: [{ id: 'Source', title: 'Stale', parentTitle: null }],
        more: false,
      }),
    )
    await flushPromises()

    expect(entries()).toEqual(['Fresh'])
  })
})
