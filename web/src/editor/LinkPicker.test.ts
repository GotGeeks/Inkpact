import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { useTreeStore } from '../stores/tree'
import type { PageNode } from '../tree/types'
import LinkPicker from './LinkPicker.vue'

const ID_PLAN = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f30'
const ID_CAFE = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f31'
const ID_HERE = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f32'
const ID_WORK = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f33'
const ID_HOME = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f34'
const ID_PLAN_2 = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f35'

const page = (id: string, title: string, parentId: string | null = null): PageNode => ({
  id,
  parentId,
  title,
  sortKey: 'a0',
  version: 1,
  updatedAt: '',
})

let wrapper: VueWrapper | null = null

beforeEach(() => {
  setActivePinia(createPinia())
  useTreeStore().pages = [
    page(ID_WORK, 'Work'),
    page(ID_HOME, 'Home'),
    page(ID_PLAN, 'Plan', ID_WORK),
    page(ID_PLAN_2, 'Plan', ID_HOME),
    page(ID_CAFE, 'Café ideas'),
    page(ID_HERE, 'This page'),
  ]
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
})

async function mountPicker(
  props: { hasLink?: boolean; pagesAvailable?: boolean } = {},
  locale: Locale = 'en',
) {
  wrapper = mount(LinkPicker, {
    props: {
      open: true,
      pageId: ID_HERE,
      hasLink: props.hasLink ?? false,
      pagesAvailable: props.pagesAvailable,
    },
    attachTo: document.body,
    global: { plugins: [createAppI18n(locale)] },
  })
  await flushPromises()
  return wrapper
}

const input = () =>
  document.body.querySelector<HTMLInputElement>('[data-testid="link-picker-input"]')!
const options = () => [...document.body.querySelectorAll<HTMLElement>('[role="option"]')]
/** The text of each option, its parts (the title, and the page it is in) with a space between them, as they are laid out. */
const labels = () =>
  options().map((option) =>
    [...option.querySelectorAll('span span')]
      .filter((part) => part.children.length === 0 || part.textContent)
      .map((part) => part.textContent!.trim())
      .filter((part, index, parts) => part && parts.indexOf(part) === index)
      .join(' '),
  )

async function type(text: string) {
  input().value = text
  input().dispatchEvent(new Event('input', { bubbles: true }))
  await flushPromises()
}

async function press(key: string) {
  input().dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  await flushPromises()
}

describe('the link picker', () => {
  it('does not expose the workspace tree offline, but still accepts a web address', async () => {
    const w = await mountPicker({ pagesAvailable: false })
    expect(options()).toEqual([])
    await type('https://example.com')
    await press('Enter')
    expect(w.emitted('choose')).toEqual([['https://example.com']])
  })

  it('removes previously offered pages when it goes offline while open', async () => {
    const w = await mountPicker()
    expect(options().length).toBeGreaterThan(0)
    await w.setProps({ pagesAvailable: false })
    await flushPromises()
    expect(options()).toEqual([])
  })

  it('is a dialog with a name and a labelled combobox, with the pages listed (not the page it is on) and the cursor in the field', async () => {
    await mountPicker()

    expect(document.body.querySelector('[role="dialog"]')).not.toBeNull()
    expect(
      document.body.querySelector('[role="dialog"]')!.getAttribute('aria-labelledby'),
    ).not.toBeNull()
    expect(input().getAttribute('role')).toBe('combobox')
    expect(input().getAttribute('aria-label')).toBe('Page or web address')
    expect(document.activeElement).toBe(input())
    expect(labels()).toEqual(['Work', 'Home', 'Plan In Work', 'Plan In Home', 'Café ideas'])
  })

  it('filters by what is typed, ignoring case and accents', async () => {
    await mountPicker()

    await type('CAFE')

    expect(labels()).toEqual(['Café ideas'])
  })

  it('says so, in words and to a screen reader, when no page matches', async () => {
    await mountPicker()

    await type('zzz')

    expect(options()).toEqual([])
    expect(document.body.querySelector('[data-testid="link-picker-empty"]')!.textContent).toContain(
      'No pages found',
    )
    expect(document.body.querySelector('[role="status"]')!.textContent).toContain('No pages found')
  })

  it('takes an Enter that comes before the first option is highlighted as a choice of the first option', async () => {
    const w = await mountPicker()

    // No time between the last key and Enter: the highlight follows the list a moment later.
    input().value = 'caf'
    input().dispatchEvent(new Event('input', { bubbles: true }))
    input().dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }),
    )
    await flushPromises()

    expect(w.emitted('choose')).toEqual([[`/p/${ID_CAFE}`]])
  })

  it('takes an Enter when no option is highlighted as a choice of the first option', async () => {
    const w = await mountPicker()
    await type('caf')
    // The options were drawn again after the highlight was asked for: nothing is highlighted.
    for (const option of options()) option.removeAttribute('data-highlighted')

    await press('Enter')

    expect(w.emitted('choose')).toEqual([[`/p/${ID_CAFE}`]])
  })

  it('leaves Enter to the option the person moved to, when the list is not changing', async () => {
    const w = await mountPicker()
    await type('plan')
    await press('ArrowDown')

    await press('Enter')

    // The second of two: not the first, which a handler that always chose the first would give.
    expect(w.emitted('choose')).toEqual([[`/p/${ID_PLAN_2}`]])
  })

  it('leaves Enter to the option the person moved to after a key that left the text as it was', async () => {
    const w = await mountPicker()
    await type('plan')
    // The same text typed over itself (select all, the same letters): an input, with nothing changed.
    await type('plan')
    await press('ArrowDown')

    await press('Enter')

    expect(w.emitted('choose')).toEqual([[`/p/${ID_PLAN_2}`]])
  })

  it('is a dialog, which the app does not open search over (Ctrl+K in the field)', async () => {
    await mountPicker()

    // The app shell opens nothing over an open `[role="dialog"]` (see "leaves another open dialog alone" in the search's tests).
    expect(input().closest('[role="dialog"]')).not.toBeNull()
  })

  it('chooses the first page with Enter, giving /p/<id>', async () => {
    const w = await mountPicker()
    await type('caf')

    await press('Enter')

    expect(w.emitted('choose')).toEqual([[`/p/${ID_CAFE}`]])
  })

  it('chooses the page that was moved to with the arrow keys', async () => {
    const w = await mountPicker()
    await type('plan')

    await press('ArrowDown')
    await press('Enter')

    expect(w.emitted('choose')).toEqual([[`/p/${ID_PLAN_2}`]])
  })

  it('chooses a page that was clicked', async () => {
    const w = await mountPicker()

    options()
      .find((option) => option.textContent!.includes('Café'))!
      .click()
    await flushPromises()

    expect(w.emitted('choose')).toEqual([[`/p/${ID_CAFE}`]])
  })

  it('offers a web address that is typed, first, and takes it as typed', async () => {
    const w = await mountPicker()
    await type('https://example.com/docs')

    expect(labels()[0]).toBe('Link to https://example.com/docs')
    await press('Enter')

    expect(w.emitted('choose')).toEqual([['https://example.com/docs']])
  })

  it('offers a mail address too', async () => {
    await mountPicker()
    await type('mailto:jos@example.com')

    expect(labels()[0]).toBe('Link to mailto:jos@example.com')
  })

  it('stores the address of a page of this app, as the address bar has it, as /p/<id>', async () => {
    const w = await mountPicker()
    await type(`${window.location.origin}/p/${ID_CAFE}`)

    await press('Enter')

    expect(w.emitted('choose')).toEqual([[`/p/${ID_CAFE}`]])
  })

  it('does not offer what the editor does not allow as a link', async () => {
    await mountPicker()

    for (const address of [
      'javascript:alert(1)',
      'data:text/html,hi',
      'vbscript:x',
      'example.com',
      '../up',
    ]) {
      await type(address)
      expect(document.body.querySelector('[data-link-kind="address"]'), address).toBeNull()
    }
  })

  it('takes the link away from text that has one, and only then offers to', async () => {
    const without = await mountPicker()
    expect(document.body.querySelector('[data-testid="link-picker-remove"]')).toBeNull()
    without.unmount()
    wrapper = null
    document.body.innerHTML = ''

    const w = await mountPicker({ hasLink: true })
    document.body.querySelector<HTMLButtonElement>('[data-testid="link-picker-remove"]')!.click()

    expect(w.emitted('remove')).toHaveLength(1)
  })

  it('says it was left without a choice on Escape and on the close button', async () => {
    const w = await mountPicker()

    document.body
      .querySelector('[role="dialog"]')!
      .dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
      )
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('button[aria-label="Close search"]')!.click()
    await flushPromises()

    expect(w.emitted('cancel')!.length).toBeGreaterThanOrEqual(1)
    expect(w.emitted('choose')).toBeUndefined()
  })

  it('starts from nothing typed each time it opens', async () => {
    const w = await mountPicker()
    await type('caf')
    expect(labels()).toEqual(['Café ideas'])

    await w.setProps({ open: false })
    await flushPromises()
    await w.setProps({ open: true })
    await flushPromises()

    expect(input().value).toBe('')
    expect(options()).toHaveLength(5)
  })

  it('does no work while it is closed, however the pages of the workspace change: it is always mounted, and they can be many', async () => {
    let reads = 0
    /** A list of pages that counts every page that is looked at. */
    const counted = (pages: PageNode[]) =>
      new Proxy(pages, {
        get(target, property, receiver) {
          if (typeof property === 'string' && /^\d+$/.test(property)) reads++
          return Reflect.get(target, property, receiver)
        },
      })
    wrapper = mount(LinkPicker, {
      props: { open: false, pageId: ID_HERE, hasLink: false },
      attachTo: document.body,
      global: { plugins: [createAppI18n('en')] },
    })
    await flushPromises()

    // The workspace changes, twice, while the picker is closed.
    useTreeStore().pages = counted([page('p-1', 'One'), page('p-2', 'Two')])
    await flushPromises()
    useTreeStore().pages = counted([
      page('p-1', 'One'),
      page('p-2', 'Two'),
      page('p-3', 'A new page'),
    ])
    await flushPromises()
    const whileClosed = reads

    await wrapper.setProps({ open: true })
    await flushPromises()

    expect(whileClosed).toBe(0)
    expect(reads).toBeGreaterThan(0)
    expect(labels()).toContain('A new page')
  })

  it('has Dutch words', async () => {
    await mountPicker({ hasLink: true }, 'nl')

    expect(input().getAttribute('aria-label')).toBe('Pagina of webadres')
    expect(
      document.body.querySelector('[data-testid="link-picker-remove"]')!.textContent,
    ).toContain('Link verwijderen')
  })
})
