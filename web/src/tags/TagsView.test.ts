import { type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mountAt } from '../testing/helpers'
import { fakeTree, outline } from '../testing/treeFixtures'
import TagsView from './TagsView.vue'

let wrapper: VueWrapper | null = null

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.unstubAllGlobals()
})

async function open(tags: Record<string, { id: string; name: string }[]>) {
  const server = fakeTree({ pages: outline(Object.keys(tags)), tags })
  const mounted = await mountAt(TagsView, '/tags')
  wrapper = mounted.wrapper
  return { server, ...mounted }
}

const rows = () => wrapper!.findAll('[data-testid="tags-list"] li').map((li) => li.text())

describe('the Tags view', () => {
  it('lists the workspace’s tags with their page counts, by name', async () => {
    await open({
      Apple: [{ id: 't1', name: 'Fruit' }],
      Banana: [
        { id: 't1', name: 'Fruit' },
        { id: 't2', name: 'Admin' },
      ],
    })

    expect(rows()).toEqual(['Admin1', 'Fruit2'])
  })

  it('says there are none yet when there are none', async () => {
    await open({})

    expect(wrapper!.text()).toContain('There are no tags yet.')
    expect(wrapper!.find('[data-testid="tags-list"]').exists()).toBe(false)
  })

  it('filters by what is typed, case and accents ignored', async () => {
    await open({
      Apple: [{ id: 't1', name: 'Café' }],
      Banana: [{ id: 't2', name: 'Admin' }],
    })

    await wrapper!.get('[data-testid="tags-filter"]').setValue('cafe')

    expect(rows()).toEqual(['Café1'])
  })

  it('links each tag to its own page', async () => {
    await open({ Apple: [{ id: 't1', name: 'Fruit' }] })

    expect(wrapper!.get('a').attributes('href')).toBe('/tags/t1')
  })
})
