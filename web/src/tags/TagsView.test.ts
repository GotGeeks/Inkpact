import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { jsonResponse, mountAt } from '../testing/helpers'
import { fakeTree, outline } from '../testing/treeFixtures'
import { useNotificationsStore } from '../stores/notifications'
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

  it('says it is loading, not that there are none, until the list is read', async () => {
    const server = fakeTree({
      pages: outline(['Apple']),
      tags: { Apple: [{ id: 't1', name: 'Fruit' }] },
    })
    const answer = server.fetch.getMockImplementation()!
    let release: () => void = () => {}
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).endsWith('/tags')) await new Promise<void>((r) => (release = r))
      return answer(input, init)
    })
    wrapper = (await mountAt(TagsView, '/tags')).wrapper

    expect(wrapper.get('[role="status"]').text()).toBe('Loading tags…')
    expect(wrapper.text()).not.toContain('There are no tags yet.')

    release()
    await flushPromises()
    expect(rows()).toEqual(['Fruit1'])
  })

  it('says the list could not be read, and posts why, when the server fails', async () => {
    const server = fakeTree({ pages: outline(['Apple']) })
    const answer = server.fetch.getMockImplementation()!
    server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) =>
      String(input).endsWith('/tags')
        ? jsonResponse(500, { code: 'server_error' })
        : answer(input, init),
    )
    wrapper = (await mountAt(TagsView, '/tags')).wrapper

    expect(wrapper.text()).toContain('The tags could not be loaded.')
    expect(wrapper.text()).not.toContain('There are no tags yet.')
    expect(useNotificationsStore().notices).toMatchObject([{ type: 'error', code: 'server_error' }])
  })

  it('says so when the filter matches nothing', async () => {
    await open({ Apple: [{ id: 't1', name: 'Fruit' }] })

    await wrapper!.get('[data-testid="tags-filter"]').setValue('zzz')

    expect(wrapper!.get('[data-testid="tags-no-match"]').text()).toBe('No tags match “zzz”.')
    expect(wrapper!.find('[data-testid="tags-list"]').exists()).toBe(false)
  })
})
