import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../App.vue'
import { useTreeStore } from '../stores/tree'
import { mountAt } from '../testing/helpers'
import { fakeTree, outline } from '../testing/treeFixtures'

let wrapper: VueWrapper | null = null

beforeEach(() => {
  localStorage.clear()
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === '(min-width: 48rem)',
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }))
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

const pages = outline(['Notes', 'Work'])
const blank = { ...pages[1]!, id: 'Blank', title: '', sortKey: 'k9' }
const trashed = { ...pages[1]!, id: 'Old', title: 'Old plans', sortKey: 'k8' }

async function app(path: string, locale: 'en' | 'nl' = 'en') {
  const server = fakeTree({ pages: [...pages, blank], trash: [trashed] })
  const mounted = await mountAt(App, path, locale)
  wrapper = mounted.wrapper
  await vi.waitFor(() => expect(useTreeStore().loaded).toBe(true))
  await flushPromises()
  return { server, ...mounted }
}

describe('the tab title', () => {
  it('names the page that is open', async () => {
    await app('/p/Work')

    expect(document.title).toBe('Work – Inkpact')
  })

  it('follows a rename and a move to another screen', async () => {
    const { router, server } = await app('/p/Work')

    // A rename elsewhere.
    server.setPages([pages[0]!, { ...pages[1]!, title: 'Work, renamed', version: 2 }, blank])
    // The tab reads the tree again, and the server's tree version moves on with every change (the fake's does not):
    // the page view reads the page again when the tree has it at another version.
    await useTreeStore().load()
    useTreeStore().treeVersion++
    await flushPromises()
    expect(document.title).toBe('Work, renamed – Inkpact')

    await router.push('/trash')
    await flushPromises()
    expect(document.title).toBe('Trash – Inkpact')
  })

  it('says Untitled for a page without a title', async () => {
    const { router } = await app('/')

    await router.push('/p/Blank')
    await flushPromises()

    expect(document.title).toBe('Untitled – Inkpact')
  })

  it('names a page in the trash by its title, as the page view shows it', async () => {
    await app('/p/Old')

    expect(document.title).toBe('Old plans – Inkpact')
  })

  it('names the screens, in Dutch too', async () => {
    const { router } = await app('/trash', 'nl')
    expect(document.title).toBe('Prullenbak – Inkpact')

    await router.push('/import/joplin')
    await flushPromises()
    expect(document.title).toBe('Importeren uit Joplin – Inkpact')
  })

  it('says a page is not found when the tree is there and the page is not', async () => {
    await app('/p/missing')

    expect(document.title).toBe('This page could not be found – Inkpact')
  })
})
