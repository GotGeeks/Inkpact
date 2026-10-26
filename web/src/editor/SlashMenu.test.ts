import type { Editor } from '@tiptap/core'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { useSyncStore } from '../stores/sync'
import { pressKey, typeText } from '../testing/editor'
import { FakeSyncHub } from '../testing/fakeSyncHub'
import PageEditor from './PageEditor.vue'

const pageId = '0a1b2c3d-0000-4000-8000-000000000003'

let hub: FakeSyncHub
let wrapper: VueWrapper | null = null

beforeEach(() => {
  hub = new FakeSyncHub()
  vi.stubGlobal('WebSocket', function () {
    return hub.createSocket()
  })
  setActivePinia(createPinia())
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  useSyncStore().destroy()
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

async function open(locale: Locale = 'en', text = '/') {
  wrapper = mount(PageEditor, {
    props: { pageId, readOnly: false },
    attachTo: document.body,
    global: { plugins: [createAppI18n(locale)] },
  })
  await flushPromises()
  const editor = (wrapper.findComponent({ name: 'EditorContent' }).props('editor') as
    Editor | undefined)!
  editor.commands.focus()
  typeText(editor, text)
  await flushPromises()
  return editor
}

const menu = () => document.querySelector<HTMLElement>('[data-testid="slash-menu"]')
const options = () => [...document.querySelectorAll<HTMLElement>('[role="option"]')]

describe('SlashMenu', () => {
  it('lists the blocks under their headers, as a listbox', async () => {
    await open()
    expect(menu()).not.toBeNull()
    const listbox = document.querySelector('[role="listbox"]')!
    expect(listbox.getAttribute('aria-label')).toBe('Insert a block')
    expect(options()).toHaveLength(11)
    expect(options()[0]!.textContent).toContain('Text')
    expect(options()[0]!.getAttribute('aria-selected')).toBe('true')
    expect(options()[1]!.getAttribute('aria-selected')).toBe('false')
    expect(
      [...document.querySelectorAll('[role="group"] > p')].map((header) =>
        header.textContent!.trim(),
      ),
    ).toEqual(['Basic', 'Lists', 'Blocks'])
    // The editor keeps the focus, and names the option that is chosen.
    expect(document.querySelector('.ProseMirror')!.getAttribute('aria-activedescendant')).toBe(
      options()[0]!.id,
    )
  })

  it('is spoken in Dutch', async () => {
    await open('nl', '/kop')
    expect(options()).toHaveLength(3)
    expect(options()[0]!.textContent).toContain('Kop 1')
    expect(document.querySelector('[role="status"]')!.textContent!.trim()).toBe(
      '3 blokken beschikbaar',
    )
    expect(document.querySelector('[role="listbox"]')!.getAttribute('aria-label')).toBe(
      'Een blok invoegen',
    )
  })

  it('says how many blocks match, and when none do', async () => {
    const editor = await open('en', '/quo')
    expect(document.querySelector('[role="status"]')!.textContent!.trim()).toBe('1 block available')

    typeText(editor, 'zz')
    await flushPromises()
    expect(options()).toHaveLength(0)
    expect(menu()!.textContent).toContain('No results')
    expect(document.querySelector('[role="status"]')!.textContent!.trim()).toBe('No results')
  })

  it('follows the arrow keys', async () => {
    const editor = await open()
    pressKey(editor, 'ArrowDown')
    await flushPromises()
    expect(options()[1]!.getAttribute('aria-selected')).toBe('true')
    expect(options()[0]!.getAttribute('aria-selected')).toBe('false')
  })

  it('makes the block that is clicked, without taking the focus from the editor', async () => {
    const editor = await open('en', '/head')
    const press = new MouseEvent('mousedown', { bubbles: true, cancelable: true })
    options()[1]!.dispatchEvent(press)
    expect(press.defaultPrevented).toBe(true)

    options()[1]!.click()
    await flushPromises()
    expect(editor.getJSON().content![0]).toEqual({ type: 'heading', attrs: { level: 2 } })
    expect(menu()).toBeNull()
  })

  it('goes away on Escape, and when the editor is read-only it never comes', async () => {
    const editor = await open()
    pressKey(editor, 'Escape')
    await flushPromises()
    expect(menu()).toBeNull()

    wrapper!.unmount()
    wrapper = mount(PageEditor, {
      props: { pageId, readOnly: true },
      attachTo: document.body,
      global: { plugins: [createAppI18n('en')] },
    })
    await flushPromises()
    const readOnly = wrapper.findComponent({ name: 'EditorContent' }).props('editor') as Editor
    typeText(readOnly, '/')
    await flushPromises()
    expect(menu()).toBeNull()
  })
})
