import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mountAt } from '../testing/helpers'
import { fakeTree, outline } from '../testing/treeFixtures'
import TagView from './TagView.vue'

let wrapper: VueWrapper | null = null

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.unstubAllGlobals()
})

async function open(
  tags: Record<string, { id: string; name: string }[]>,
  options: {
    role?: 'owner' | 'editor' | 'viewer'
    trashedTags?: Record<string, { id: string; name: string }[]>
  } = {},
) {
  const trashed = options.trashedTags ?? {}
  const server = fakeTree({
    pages: outline(Object.keys(tags)),
    trash: outline(Object.keys(trashed)),
    tags: { ...tags, ...trashed },
    role: options.role,
  })
  const mounted = await mountAt(TagView, '/tags/t1')
  wrapper = mounted.wrapper
  return { server, ...mounted }
}

describe('a tag’s page', () => {
  it('shows its name and the live pages that have it, by title', async () => {
    await open({
      banana: [{ id: 't1', name: 'Fruit' }],
      Apple: [{ id: 't1', name: 'Fruit' }],
    })

    expect(wrapper!.get('input').element.value).toBe('Fruit')
    expect(wrapper!.findAll('[data-testid="tag-pages-list"] li').map((li) => li.text())).toEqual([
      'Apple',
      'banana',
    ])
  })

  it('shows a page’s parent, so two pages with one title can be told apart', async () => {
    const pages = outline(['Notebook', '  banana'])
    const server = fakeTree({
      pages,
      tags: { banana: [{ id: 't1', name: 'Fruit' }] },
    })
    const mounted = await mountAt(TagView, '/tags/t1')
    wrapper = mounted.wrapper
    void server

    expect(wrapper!.findAll('[data-testid="tag-pages-list"] li').map((li) => li.text())).toEqual([
      'bananaNotebook',
    ])
  })

  it('says there are none when the tag is only on a page in the trash', async () => {
    await open({}, { trashedTags: { Apple: [{ id: 't1', name: 'Fruit' }] } })

    expect(wrapper!.text()).toContain('No pages have this tag.')
    expect(wrapper!.find('[data-testid="tag-pages-list"]').exists()).toBe(false)
  })

  it('shows the not-found message for a tag that does not exist', async () => {
    await open({ Apple: [{ id: 't2', name: 'Other' }] })

    expect(wrapper!.text()).toContain('This tag could not be found')
    expect(wrapper!.get('a').attributes('href')).toBe('/tags')
  })

  it('renames the tag when an editor changes the field and leaves it', async () => {
    const { server } = await open({ Apple: [{ id: 't1', name: 'Fruit' }] })

    await wrapper!.get('input').setValue('Produce')
    await wrapper!.get('input').trigger('blur')
    await flushPromises()

    expect(server.writes()).toEqual([
      { method: 'PATCH', path: '/api/workspaces/w1/tags/t1', body: { name: 'Produce' } },
    ])
  })

  it('shows the name as plain text, not a field, for a viewer', async () => {
    await open({ Apple: [{ id: 't1', name: 'Fruit' }] }, { role: 'viewer' })

    expect(wrapper!.find('input').exists()).toBe(false)
    expect(wrapper!.get('h1').text()).toBe('Fruit')
  })
})
