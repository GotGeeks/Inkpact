import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { createAppI18n } from '../i18n'
import { useFavoritesStore } from '../stores/favorites'
import { useTreeStore } from '../stores/tree'
import { fakeTree, outline } from '../testing/treeFixtures'
import FavoritesList from './FavoritesList.vue'

let wrapper: VueWrapper | null = null

beforeEach(() => localStorage.clear())
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.unstubAllGlobals()
})

async function list(favorites: string[], selectedId: string | null = null) {
  fakeTree({ pages: outline(['A', 'B', 'C']), favorites })
  const pinia = createPinia()
  setActivePinia(pinia)
  const tree = useTreeStore()
  await tree.init()
  await useFavoritesStore().load()
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/p/:pageId', name: 'page', component: { template: '<div />' } }],
  })
  const onNavigate = vi.fn()
  wrapper = mount(FavoritesList, {
    props: { selectedId, onNavigate },
    global: { plugins: [pinia, router, createAppI18n('en')] },
  })
  await flushPromises()
  return { tree, onNavigate }
}

describe('FavoritesList', () => {
  it('is not there when there are no favorites', async () => {
    await list([])

    expect(wrapper!.find('nav').exists()).toBe(false)
  })

  it('is a labelled nav with the favorites in the order they were starred, with titles from the tree', async () => {
    const { tree } = await list(['C', 'A'], 'A')

    expect(wrapper!.get('nav').attributes('aria-label')).toBe('Favorites')
    expect(wrapper!.findAll('a').map((a) => a.text())).toEqual(['C', 'A'])
    expect(wrapper!.findAll('a')[1]!.attributes('aria-current')).toBe('page')

    // A rename shows at once, from the tree.
    tree.pages = tree.pages.map((p) => (p.id === 'C' ? { ...p, title: 'Renamed' } : p))
    await flushPromises()
    expect(wrapper!.findAll('a')[0]!.text()).toBe('Renamed')
  })

  it('leaves out an id the tree does not have', async () => {
    await list(['gone', 'B'])

    expect(wrapper!.findAll('a').map((a) => a.text())).toEqual(['B'])
  })

  it('tells the drawer to make way when a favorite is opened', async () => {
    const { onNavigate } = await list(['B'])

    await wrapper!.get('a').trigger('click')

    expect(onNavigate).toHaveBeenCalledOnce()
  })
})
