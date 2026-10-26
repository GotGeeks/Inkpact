import type { Editor } from '@tiptap/core'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../i18n'
import { useSyncStore } from '../stores/sync'
import { FakeSyncHub } from '../testing/fakeSyncHub'
import PageEditor from './PageEditor.vue'

const pageId = '0a1b2c3d-0000-4000-8000-000000000004'

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

/** TipTap tells Vue about a new editor state after two animation frames; wait as long as the screen does. */
async function settle() {
  await flushPromises()
  await new Promise<void>((resolve) =>
    requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
  )
  await flushPromises()
}

async function openTable(locale: 'en' | 'nl' = 'en'): Promise<Editor> {
  wrapper = mount(PageEditor, {
    props: { pageId, readOnly: false },
    attachTo: document.body,
    global: { plugins: [createAppI18n(locale)] },
  })
  await flushPromises()
  const editor = (wrapper.findComponent({ name: 'EditorContent' }).props('editor') as
    Editor | undefined)!
  editor.view.dom.tabIndex = 0
  editor.view.dom.focus()
  editor.commands.insertTable({ rows: 3, cols: 3, withHeaderRow: true })
  await settle()
  return editor
}

const menu = () => document.querySelector<HTMLElement>('[data-testid="table-menu"]')
const action = (id: string) =>
  document.querySelector<HTMLButtonElement>(`[data-testid="table-${id}"]`)!
const rows = (editor: Editor) => editor.state.doc.firstChild!.childCount
const columns = (editor: Editor) => editor.state.doc.firstChild!.firstChild!.childCount

describe('the table menu', () => {
  it('shows while the caret is in a table, and not outside it', async () => {
    const editor = await openTable()
    expect(menu()).not.toBeNull()
    expect(menu()!.getAttribute('aria-label')).toBe('Table')

    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    await settle()
    expect(editor.isActive('table')).toBe(false)
    expect(menu()).toBeNull()
  })

  it('has a name on every button, in English and Dutch', async () => {
    await openTable('nl')
    expect(action('addRowAfter').textContent!.trim()).toBe('Rij eronder toevoegen')
    expect(action('deleteTable').textContent!.trim()).toBe('Tabel verwijderen')
    for (const button of menu()!.querySelectorAll('button')) {
      expect(button.textContent!.trim()).not.toMatch(/^editor\./)
    }
  })

  it('adds rows and columns before and after the caret', async () => {
    const editor = await openTable()
    action('addRowAfter').click()
    await settle()
    expect(rows(editor)).toBe(4)
    action('addRowBefore').click()
    await settle()
    expect(rows(editor)).toBe(5)
    action('addColumnAfter').click()
    await settle()
    expect(columns(editor)).toBe(4)
    action('addColumnBefore').click()
    await settle()
    expect(columns(editor)).toBe(5)
  })

  it('deletes the row and the column of the caret', async () => {
    const editor = await openTable()
    action('deleteRow').click()
    await settle()
    expect(rows(editor)).toBe(2)
    action('deleteColumn').click()
    await settle()
    expect(columns(editor)).toBe(2)
  })

  it('keeps the first row a header until it is deleted', async () => {
    const editor = await openTable()
    const first = () => editor.state.doc.firstChild!.firstChild!.firstChild!.type.name
    expect(first()).toBe('tableHeader')
    action('addRowAfter').click()
    await settle()
    expect(first()).toBe('tableHeader')
    expect(editor.state.doc.firstChild!.child(1).firstChild!.type.name).toBe('tableCell')
  })

  it('removes the table, and the page keeps a place to type', async () => {
    const editor = await openTable()
    action('deleteTable').click()
    await settle()
    expect(editor.state.doc.firstChild!.type.name).toBe('paragraph')
    expect(menu()).toBeNull()
  })

  it('removes the table when its last row is deleted', async () => {
    const editor = await openTable()
    for (let count = 0; count < 3; count++) {
      action('deleteRow').click()
      await settle()
    }
    expect(editor.state.doc.firstChild!.type.name).not.toBe('table')
  })

  it('does not take the focus from the table when pressed', async () => {
    await openTable()
    for (const type of ['pointerdown', 'mousedown']) {
      const event = new Event(type, { bubbles: true, cancelable: true })
      action('deleteRow').dispatchEvent(event)
      expect(event.defaultPrevented, type).toBe(true)
    }
  })
})
