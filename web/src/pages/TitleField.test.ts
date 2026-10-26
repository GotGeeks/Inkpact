import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { useTreeStore } from '../stores/tree'
import TitleField from './TitleField.vue'

let wrapper: VueWrapper | null = null
let rename: ReturnType<typeof vi.fn>

beforeEach(() => {
  vi.useFakeTimers()
  setActivePinia(createPinia())
  rename = vi.fn().mockResolvedValue({})
  useTreeStore().rename = rename as never
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.useRealTimers()
})

function mountField(
  props: Partial<{ title: string; readOnly: boolean }> = {},
  locale: Locale = 'en',
) {
  wrapper = mount(TitleField, {
    props: { pageId: 'p1', title: 'Notes', readOnly: false, ...props },
    attachTo: document.body,
    global: { plugins: [createAppI18n(locale)] },
  })
  return wrapper
}

const field = () => wrapper!.get('textarea')

async function typeTitle(value: string) {
  await field().setValue(value)
}

describe('TitleField', () => {
  it('shows the title of the page, with a name for screen readers and "Untitled" as the placeholder', () => {
    mountField({ title: 'Notes' })
    expect(field().element.value).toBe('Notes')
    expect(field().attributes('aria-label')).toBe('Title')
    expect(field().attributes('placeholder')).toBe('Untitled')
  })

  it('has Dutch words', () => {
    mountField({}, 'nl')
    expect(field().attributes('aria-label')).toBe('Titel')
    expect(field().attributes('placeholder')).toBe('Zonder titel')
  })

  it('saves a second after the last key, not on every key', async () => {
    mountField()
    await typeTitle('Notes 2')
    await vi.advanceTimersByTimeAsync(900)
    await typeTitle('Notes 2025')
    await vi.advanceTimersByTimeAsync(900)
    expect(rename).not.toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(200)
    expect(rename).toHaveBeenCalledTimes(1)
    expect(rename).toHaveBeenCalledWith('p1', 'Notes 2025')
  })

  it('saves on Enter and tells the page to move to the editor', async () => {
    mountField()
    await typeTitle('Renamed')
    await field().trigger('keydown', { key: 'Enter' })

    expect(rename).toHaveBeenCalledWith('p1', 'Renamed')
    expect(wrapper!.emitted('enter')).toHaveLength(1)
    // And not again when the timer would have run.
    await vi.advanceTimersByTimeAsync(2000)
    expect(rename).toHaveBeenCalledTimes(1)
  })

  it('saves when the field loses focus', async () => {
    mountField()
    await typeTitle('Renamed')
    await field().trigger('blur')
    expect(rename).toHaveBeenCalledWith('p1', 'Renamed')
  })

  it('does not save a title that did not change', async () => {
    mountField({ title: 'Notes' })
    await field().trigger('keydown', { key: 'Enter' })
    await field().trigger('blur')
    await typeTitle('Notes')
    await vi.advanceTimersByTimeAsync(2000)
    expect(rename).not.toHaveBeenCalled()
  })

  it('makes a title one line: a line break typed or pasted becomes a space', async () => {
    mountField()
    await typeTitle('first\nsecond')
    expect(field().element.value).toBe('first second')
    await typeTitle('a \r\n b')
    expect(field().element.value).toBe('a b')
  })

  it('does not put a line break in with Enter', async () => {
    mountField()
    await field().trigger('keydown', { key: 'Enter' })
    expect(field().element.value).toBe('Notes')
  })

  it('cannot be changed, and does not save, when the person may not rename', async () => {
    mountField({ readOnly: true })
    expect(field().attributes('readonly')).toBeDefined()
    await typeTitle('Changed')
    await field().trigger('blur')
    await vi.advanceTimersByTimeAsync(2000)
    expect(rename).not.toHaveBeenCalled()
  })

  it('saves a title that is still waiting when the page is left', async () => {
    mountField()
    await typeTitle('Late')
    wrapper!.unmount()
    wrapper = null
    await flushPromises()
    expect(rename).toHaveBeenCalledWith('p1', 'Late')
  })

  it('follows a rename made elsewhere, unless the person is typing here', async () => {
    mountField({ title: 'Notes' })
    await wrapper!.setProps({ title: 'Renamed elsewhere' })
    expect(field().element.value).toBe('Renamed elsewhere')

    field().element.focus()
    await typeTitle('Mine')
    await wrapper!.setProps({ title: 'Another rename' })
    expect(field().element.value).toBe('Mine')
  })
})
