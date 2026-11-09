import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../i18n'
import { useFavoritesStore } from '../stores/favorites'
import { useTreeStore } from '../stores/tree'
import { fakeTree, outline } from '../testing/treeFixtures'
import FavoriteButton from './FavoriteButton.vue'

let wrapper: VueWrapper | null = null

beforeEach(() => localStorage.clear())
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.unstubAllGlobals()
})

async function button(favorites: string[] = [], props: { inTrash?: boolean } = {}) {
  const server = fakeTree({ pages: outline(['A']), favorites })
  const pinia = createPinia()
  setActivePinia(pinia)
  await useTreeStore().init()
  await useFavoritesStore().load()
  wrapper = mount(FavoriteButton, {
    props: { pageId: 'A', ...props },
    global: { plugins: [pinia, createAppI18n('en')] },
  })
  return { server, el: wrapper.get('button') }
}

describe('FavoriteButton', () => {
  it('is a toggle with a label that does not change and a state that does', async () => {
    const { el } = await button()

    expect(el.attributes('aria-label')).toBe('Favorite')
    expect(el.attributes('aria-pressed')).toBe('false')

    await el.trigger('click')
    await flushPromises()

    expect(el.attributes('aria-label')).toBe('Favorite')
    expect(el.attributes('aria-pressed')).toBe('true')
  })

  it('starts pressed for a page that is a favorite, and a click unstars it', async () => {
    const { el, server } = await button(['A'])

    expect(el.attributes('aria-pressed')).toBe('true')
    await el.trigger('click')
    await flushPromises()

    expect(el.attributes('aria-pressed')).toBe('false')
    expect(server.writes()[0]).toMatchObject({ method: 'DELETE', path: '/api/pages/A/favorite' })
  })

  it('cannot star a page in the trash', async () => {
    const { el } = await button([], { inTrash: true })

    expect(el.attributes('disabled')).toBeDefined()
  })
})
