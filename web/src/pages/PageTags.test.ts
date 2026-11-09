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
  const mounted = await mountAt(Host('Target', tags, readOnly), '/', locale, { attach: true })
  wrapper = mounted.wrapper
  return { server, ...mounted }
}

const pillNames = () => wrapper!.findAll('[data-testid="tag-pill"]').map((li) => li.text())

/**
 * Holds back the server's answers to the requests `matches` picks, until the test lets each go: every other request is
 * answered at once, as before.
 */
function holdBack(
  server: ReturnType<typeof fakeTree>,
  matches: (method: string, path: string) => boolean,
) {
  const answer = server.fetch.getMockImplementation()!
  const held: Array<() => void> = []
  server.fetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    if (!matches(init?.method ?? 'GET', String(input))) return answer(input, init)
    await new Promise<void>((release) => held.push(release))
    return answer(input, init)
  })
  return {
    count: () => held.length,
    releaseNext: async () => {
      held.shift()?.()
      await flushPromises()
    },
  }
}

async function openField() {
  await wrapper!.get('[data-testid="add-tag-button"]').trigger('click')
  await flushPromises()
  return wrapper!.get('[data-testid="add-tag-field"]')
}

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

  // ---- review fixes ----------------------------------------------------------------------------------------------

  it('sends one change at a time, so an older answer can never replace a newer one', async () => {
    const { server } = await open({ tags: { Target: [{ id: 't1', name: 'Work' }] } }, [
      { id: 't1', name: 'Work' },
    ])
    const posts = holdBack(server, (method, path) => method === 'POST' && path.endsWith('/tags'))

    await wrapper!.get('[aria-label="Remove tag Work"]').trigger('click')
    await openField().then((field) => field.setValue('Home'))
    await wrapper!.get('[data-testid="add-tag-field"]').trigger('keydown', { key: 'Enter' })
    await flushPromises()

    // The add waits until the remove has been answered.
    expect(posts.count()).toBe(1)
    await posts.releaseNext()
    expect(posts.count()).toBe(1)
    await posts.releaseNext()

    expect(server.writes().map((w) => w.body)).toEqual([{ remove: ['t1'] }, { add: ['Home'] }])
    expect(pillNames()).toEqual(['Home'])
  })

  it('reads the tags again after its own change, never in the middle of one', async () => {
    const { server } = await open({}, [])
    const posts = holdBack(server, (method, path) => method === 'POST' && path.endsWith('/tags'))
    const field = await openField()
    await field.setValue('Travel')
    await field.trigger('keydown', { key: 'Enter' })
    await flushPromises()

    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true })
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    const readsWhileHeld = server.fetch.mock.calls.filter(
      ([input]) => String(input) === '/api/pages/Target',
    ).length
    await posts.releaseNext()

    expect(readsWhileHeld).toBe(0)
    expect(pillNames()).toEqual(['Travel'])
  })

  it('reads the tags again when a change fails, and says why', async () => {
    const { server } = await open({ tags: { Target: [{ id: 't1', name: 'Work' }] } }, [])
    server.failNext(409, 'page_in_trash')

    const field = await openField()
    await field.setValue('Home')
    await field.trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(pillNames()).toEqual(['Work'])
  })

  it('starts at the first option again when the text changes, so Enter takes what is now first', async () => {
    const { server } = await open(
      {
        tags: {
          Other: [
            { id: 't1', name: 'Wood' },
            { id: 't2', name: 'Work' },
            { id: 't3', name: 'Workout' },
          ],
        },
      },
      [],
    )
    const field = await openField()
    await field.setValue('W')
    await flushPromises()
    await field.trigger('keydown', { key: 'ArrowDown' })
    await field.trigger('keydown', { key: 'ArrowDown' })
    expect(wrapper!.get('[aria-selected="true"]').text()).toBe('Work')

    // Still four options, so the old index would still point at one: "Work", not what was typed.
    await field.setValue('Wo')
    await flushPromises()
    expect(wrapper!.findAll('[role="option"]')).toHaveLength(4)
    await field.trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(server.writes().map((w) => w.body)).toEqual([{ add: ['Wo'] }])
  })

  it('offers to make a tag that differs from one only by its accents, as the server allows', async () => {
    await open({ tags: { Other: [{ id: 't1', name: 'café' }] } }, [])
    const field = await openField()
    await field.setValue('cafe')
    await flushPromises()

    expect(wrapper!.findAll('[role="option"]').map((o) => o.text())).toEqual([
      'Create tag “cafe”',
      'café',
    ])
  })

  it('focuses the field at once, before the workspace’s tags are read', async () => {
    const { server } = await open({}, [])
    const reads = holdBack(server, (method, path) => method === 'GET' && path.endsWith('/tags'))

    await wrapper!.get('[data-testid="add-tag-button"]').trigger('click')
    await flushPromises()

    expect(reads.count()).toBe(1)
    expect(document.activeElement).toBe(wrapper!.get('[data-testid="add-tag-field"]').element)
    await reads.releaseNext()
  })

  it('gives the focus back to "Add tag" after Escape', async () => {
    await open({}, [])
    const field = await openField()

    await field.trigger('keydown', { key: 'Escape' })
    await flushPromises()

    expect(document.activeElement).toBe(wrapper!.get('[data-testid="add-tag-button"]').element)
  })

  it('moves the focus to the next pill’s remove button when one is removed, and to "Add tag" after the last', async () => {
    const both = [
      { id: 't1', name: 'Home' },
      { id: 't2', name: 'Work' },
    ]
    await open({ tags: { Target: both } }, both)
    const remove = (name: string) => wrapper!.get(`[aria-label="Remove tag ${name}"]`)

    ;(remove('Home').element as HTMLElement).focus()
    await remove('Home').trigger('click')
    // The button that was clicked is gone; a real browser drops the focus to the page.
    ;(document.activeElement as HTMLElement | null)?.blur()
    await flushPromises()
    expect(document.activeElement).toBe(remove('Work').element)

    await remove('Work').trigger('click')
    ;(document.activeElement as HTMLElement | null)?.blur()
    await flushPromises()
    expect(document.activeElement).toBe(wrapper!.get('[data-testid="add-tag-button"]').element)
  })

  it('leaves Enter to an input method that is composing a word', async () => {
    const { server } = await open({}, [])
    const field = await openField()
    await field.setValue('とうきょう')

    await field.trigger('keydown', { key: 'Enter', isComposing: true })
    await flushPromises()

    expect(server.writes()).toEqual([])
  })
})
