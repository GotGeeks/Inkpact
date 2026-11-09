import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { mountAt } from '../testing/helpers'
import { fakeTree, outline } from '../testing/treeFixtures'
import PageTags from './PageTags.vue'

let wrapper: VueWrapper | null = null

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

interface Tag {
  id: string
  name: string
}

const Host = (pageId: string, tags: Tag[], readOnly = false) =>
  defineComponent({ render: () => h(PageTags, { pageId, tags, readOnly }) })

async function open(
  options: Parameters<typeof fakeTree>[0],
  tags: Tag[] = [],
  readOnly = false,
  locale: 'en' | 'nl' = 'en',
) {
  const server = fakeTree({ pages: outline(['Target', 'Other']), ...options })
  const mounted = await mountAt(Host('Target', tags, readOnly), '/', locale)
  wrapper = mounted.wrapper
  return { server, ...mounted }
}

const pillNames = () => wrapper!.findAll('[data-testid="tag-pill"]').map((li) => li.text())

describe('a page’s tags', () => {
  it('shows each as a pill linking to that tag’s page, with an add button', async () => {
    await open({}, [
      { id: 't1', name: 'Work' },
      { id: 't2', name: 'Home' },
    ])

    expect(pillNames()).toEqual(['Work', 'Home'])
    expect(wrapper!.findAll('a').map((a) => a.attributes('href'))).toEqual(['/tags/t1', '/tags/t2'])
    expect(wrapper!.get('[data-testid="add-tag-button"]').text()).toBe('Add tag')
  })

  it('shows nothing at all when there are no tags and the person may not change them', async () => {
    await open({}, [], true)

    expect(wrapper!.find('[role="list"]').exists()).toBe(false)
  })

  it('shows pills without a remove button, and no add button, when the person may not change them', async () => {
    await open({}, [{ id: 't1', name: 'Work' }], true)

    expect(pillNames()).toEqual(['Work'])
    expect(wrapper!.find('button').exists()).toBe(false)
  })

  it('removes a tag when its remove button is pressed', async () => {
    const { server } = await open({}, [{ id: 't1', name: 'Work' }])

    await wrapper!.get('[aria-label="Remove tag Work"]').trigger('click')
    await flushPromises()

    expect(pillNames()).toEqual([])
    expect(server.writes()).toEqual([
      { method: 'POST', path: '/api/pages/Target/tags', body: { remove: ['t1'] } },
    ])
  })

  it('offers the workspace’s tags and a way to create a new one, filtered by what is typed', async () => {
    await open({ tags: { Other: [{ id: 't1', name: 'Work' }] } }, [])

    await wrapper!.get('[data-testid="add-tag-button"]').trigger('click')
    await flushPromises()
    const field = wrapper!.get('[data-testid="add-tag-field"]')
    await field.setValue('Wo')
    await flushPromises()

    // Both: the existing tag it matches, and the option to make a new one with exactly what was typed.
    expect(wrapper!.findAll('[role="option"]').map((o) => o.text())).toEqual([
      'Create tag “Wo”',
      'Work',
    ])

    await field.setValue('Work')
    await flushPromises()

    // Typed in full: only the existing tag, since making a new one would just be a duplicate.
    expect(wrapper!.findAll('[role="option"]').map((o) => o.text())).toEqual(['Work'])

    await field.setValue('Travel')
    await flushPromises()

    expect(wrapper!.findAll('[role="option"]').map((o) => o.text())).toEqual([
      'Create tag “Travel”',
    ])
  })

  it('adds a tag by clicking an option, and keeps the field open for the next one', async () => {
    const { server } = await open({ tags: { Other: [{ id: 't1', name: 'Work' }] } }, [])

    await wrapper!.get('[data-testid="add-tag-button"]').trigger('click')
    await flushPromises()
    await wrapper!.get('[data-testid="add-tag-field"]').setValue('Work')
    await flushPromises()
    await wrapper!.get('[role="option"]').trigger('click')
    await flushPromises()

    expect(server.writes()).toEqual([
      { method: 'POST', path: '/api/pages/Target/tags', body: { add: ['Work'] } },
    ])
    expect(pillNames()).toEqual(['Work'])
    // The field is still open, ready for another tag.
    expect(wrapper!.find('[data-testid="add-tag-field"]').exists()).toBe(true)
  })

  it('adds a new tag by typing a name and pressing Enter', async () => {
    const { server } = await open({}, [])

    await wrapper!.get('[data-testid="add-tag-button"]').trigger('click')
    await flushPromises()
    const field = wrapper!.get('[data-testid="add-tag-field"]')
    await field.setValue('Travel')
    await flushPromises()
    await field.trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(server.writes()).toEqual([
      { method: 'POST', path: '/api/pages/Target/tags', body: { add: ['Travel'] } },
    ])
    expect(pillNames()).toEqual(['Travel'])
  })

  it('closes the field with Escape, keeping what was there before', async () => {
    await open({}, [{ id: 't1', name: 'Work' }])

    await wrapper!.get('[data-testid="add-tag-button"]').trigger('click')
    await flushPromises()
    await wrapper!.get('[data-testid="add-tag-field"]').trigger('keydown', { key: 'Escape' })
    await flushPromises()

    expect(wrapper!.find('[data-testid="add-tag-field"]').exists()).toBe(false)
    expect(pillNames()).toEqual(['Work'])
  })

  it('reads the tags again when the tab comes back, so a tag added since is found', async () => {
    // The page opened with no tags; one was added elsewhere since (another tab, or the import).
    await open({ tags: { Target: [{ id: 't1', name: 'Work' }] } }, [])
    expect(pillNames()).toEqual([])

    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true })
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()

    expect(pillNames()).toEqual(['Work'])
  })

  it('is not asked again while the tab is hidden', async () => {
    const { server } = await open({ tags: { Target: [{ id: 't1', name: 'Work' }] } }, [])
    const readsBefore = server.writes().length

    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true })
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()

    expect(pillNames()).toEqual([])
    expect(server.writes().length).toBe(readsBefore)
  })
})
