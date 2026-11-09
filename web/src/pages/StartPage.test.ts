import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { createAppI18n } from '../i18n'
import { useTreeStore } from '../stores/tree'
import { fakeTree, outline } from '../testing/treeFixtures'
import StartPage from './StartPage.vue'

let wrapper: VueWrapper | null = null

beforeEach(() => {
  localStorage.clear()
  vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-11-09T12:00:00Z') })
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

async function start(options: Parameters<typeof fakeTree>[0] = {}, locale: 'en' | 'nl' = 'en') {
  const server = fakeTree(options)
  const pinia = createPinia()
  setActivePinia(pinia)
  await useTreeStore().init()
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/p/:pageId', name: 'page', component: { template: '<div />' } },
    ],
  })
  await router.push('/')
  wrapper = mount(StartPage, { global: { plugins: [pinia, router, createAppI18n(locale)] } })
  await flushPromises()
  return { server, router }
}

const items = () => wrapper!.findAll('[data-testid="recent-pages"] li')

describe('StartPage: recently opened', () => {
  it('lists the pages opened last, newest first, with their parent and when', async () => {
    await start({
      pages: outline(['Notebook', '  Note', 'Other']),
      recent: [
        { pageId: 'Note', openedAt: '2026-11-09T11:55:00Z' },
        { pageId: 'Other', openedAt: '2026-11-08T12:00:00Z' },
      ],
    })

    expect(wrapper!.get('h2').text()).toBe('Recently opened')
    expect(items().map((li) => li.text())).toEqual([
      'Notein Notebook5 minutes ago',
      'Otheryesterday',
    ])
    expect(items()[0]!.get('a').attributes('href')).toBe('/p/Note')
  })

  it('moves the times on while the page stays open', async () => {
    vi.useFakeTimers({
      toFake: ['Date', 'setInterval', 'clearInterval'],
      now: new Date('2026-11-09T12:00:00Z'),
    })
    await start({
      pages: outline(['A']),
      recent: [{ pageId: 'A', openedAt: '2026-11-09T11:55:00Z' }],
    })
    expect(items()[0]!.text()).toContain('5 minutes ago')

    vi.advanceTimersByTime(60 * 60 * 1000)
    await flushPromises()

    expect(items()[0]!.text()).toContain('1 hour ago')
  })

  it('says it in Dutch', async () => {
    await start(
      {
        pages: outline(['A']),
        recent: [{ pageId: 'A', openedAt: '2026-11-09T11:55:00Z' }],
      },
      'nl',
    )

    expect(wrapper!.get('h2').text()).toBe('Onlangs geopend')
    expect(items()[0]!.text()).toContain('5 minuten geleden')
  })

  it('shows the ten newest, and leaves out a page the tree does not have', async () => {
    const titles = Array.from({ length: 12 }, (_, i) => `P${i}`)
    await start({
      pages: outline(titles),
      recent: [
        { pageId: 'unknown', openedAt: '2026-11-09T11:59:00Z' },
        ...titles.map((pageId) => ({ pageId, openedAt: '2026-11-09T11:00:00Z' })),
      ],
    })

    expect(items()).toHaveLength(10)
    expect(items()[0]!.text()).toContain('P0')
  })

  it('has no list when nothing was opened, and keeps the text and the button', async () => {
    await start({ pages: outline(['A']) })

    expect(wrapper!.find('[data-testid="recent-pages"]').exists()).toBe(false)
    expect(wrapper!.text()).toContain('Pick a page in the sidebar')
    expect(wrapper!.get('button').text()).toBe('New page')
  })

  it('shows the empty text for a workspace without pages', async () => {
    await start()

    expect(wrapper!.text()).toContain('You have no pages yet')
  })
})
