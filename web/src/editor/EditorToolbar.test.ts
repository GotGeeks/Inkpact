import type { Editor } from '@tiptap/core'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import * as Y from 'yjs'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../i18n'
import { useSyncStore } from '../stores/sync'
import { typeText } from '../testing/editor'
import { FakeSyncHub } from '../testing/fakeSyncHub'
import { FIELD } from './firstParagraph'
import PageEditor from './PageEditor.vue'

const pageId = '0a1b2c3d-0000-4000-8000-000000000002'

let hub: FakeSyncHub
let wrapper: VueWrapper | null = null

/** What the browser says about the screen: a phone has a coarse pointer and no room. */
function stubScreen({ coarse, wide }: { coarse: boolean; wide: boolean }) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('coarse') ? coarse : query.includes('min-width') ? wide : false,
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }))
}

beforeEach(() => {
  hub = new FakeSyncHub()
  vi.stubGlobal('WebSocket', function () {
    return hub.createSocket()
  })
  setActivePinia(createPinia())
  stubScreen({ coarse: true, wide: false })
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  useSyncStore().destroy()
  document.body.innerHTML = ''
  document.documentElement.style.removeProperty('--keyboard-inset')
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

async function mountEditor(locale: 'en' | 'nl' = 'en', readOnly = false) {
  wrapper = mount(PageEditor, {
    props: { pageId, readOnly },
    attachTo: document.body,
    global: { plugins: [createAppI18n(locale)] },
  })
  await settle()
  return wrapper
}

function editorOf(w: VueWrapper): Editor {
  return (w.findComponent({ name: 'EditorContent' }).props('editor') as Editor | undefined)!
}

/** Gives the editor the focus the way a tap does, and lets the toolbar follow. */
async function focusEditor(w: VueWrapper) {
  const editor = editorOf(w)
  // jsdom only lets an element with a tab index take the focus; a browser does so for what can be edited.
  editor.view.dom.tabIndex = 0
  editor.view.dom.focus()
  await settle()
  return editor
}

/**
 * TipTap tells Vue about a new editor state after two animation frames, so a button follows a change a moment later;
 * a test has to wait as long as the screen does.
 */
async function settle() {
  await flushPromises()
  await new Promise<void>((resolve) =>
    requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
  )
  await flushPromises()
}

const toolbar = () => document.querySelector<HTMLElement>('[data-testid="editor-toolbar"]')
const button = (id: string) =>
  document.querySelector<HTMLButtonElement>(`[data-testid="toolbar-${id}"]`)!
const slashMenu = () => document.querySelector('[data-testid="slash-menu"]')

function wireUpdate(page: string, update: Uint8Array): Uint8Array {
  const id = new TextEncoder().encode(page)
  const length: number[] = []
  let value = update.length
  while (value > 0x7f) {
    length.push(0x80 | (value & 0x7f))
    value = Math.floor(value / 128)
  }
  length.push(value)
  return new Uint8Array([2, id.length, ...id, ...length, ...update])
}

describe('the mobile toolbar', () => {
  it('shows while the editor has the focus on a phone, and not before', async () => {
    const w = await mountEditor()
    expect(toolbar()).toBeNull()

    await focusEditor(w)
    expect(toolbar()).not.toBeNull()
    expect(toolbar()!.getAttribute('role')).toBe('toolbar')
    expect(toolbar()!.getAttribute('aria-label')).toBe('Formatting')
  })

  it('never shows on a wide screen with a mouse', async () => {
    stubScreen({ coarse: false, wide: true })
    const w = await mountEditor()
    await focusEditor(w)
    expect(toolbar()).toBeNull()
  })

  it('shows on a narrow window even with a mouse, and on a wide touch screen', async () => {
    stubScreen({ coarse: false, wide: false })
    let w = await mountEditor()
    await focusEditor(w)
    expect(toolbar()).not.toBeNull()
    w.unmount()
    wrapper = null

    stubScreen({ coarse: true, wide: true })
    w = await mountEditor()
    await focusEditor(w)
    expect(toolbar()).not.toBeNull()
  })

  it('goes away when the editor loses the focus', async () => {
    const w = await mountEditor()
    const editor = await focusEditor(w)
    expect(toolbar()).not.toBeNull()

    editor.commands.blur()
    await vi.waitFor(() => expect(toolbar()).toBeNull())
  })

  it('never shows for a viewer who cannot edit', async () => {
    const w = await mountEditor('en', true)
    await focusEditor(w)
    expect(toolbar()).toBeNull()
  })

  it('has a name on every button, in both languages', async () => {
    for (const [locale, undo] of [
      ['en', 'Undo'],
      ['nl', 'Ongedaan maken'],
    ] as const) {
      const w = await mountEditor(locale)
      await focusEditor(w)
      const buttons = [...toolbar()!.querySelectorAll('button')]
      expect(buttons.length).toBeGreaterThanOrEqual(9)
      for (const each of buttons) {
        expect(each.getAttribute('aria-label'), each.outerHTML).toBeTruthy()
        expect(each.getAttribute('aria-label')).not.toMatch(/^editor\./)
      }
      expect(button('undo').getAttribute('aria-label')).toBe(undo)
      w.unmount()
      wrapper = null
    }
  })

  it('keeps the editor focused when a button is pressed, so the keyboard stays open', async () => {
    const w = await mountEditor()
    await focusEditor(w)
    const press = new Event('mousedown', { bubbles: true, cancelable: true })
    button('bold').dispatchEvent(press)
    expect(press.defaultPrevented).toBe(true)
    // A cancelled pointerdown stops iOS Safari from sending the click, so the button would do nothing on an iPhone.
    const touch = new Event('pointerdown', { bubbles: true, cancelable: true })
    button('bold').dispatchEvent(touch)
    expect(touch.defaultPrevented).toBe(false)
  })

  it('has one tab stop, and the arrow keys move along the buttons', async () => {
    const w = await mountEditor()
    await focusEditor(w)
    const stops = () => [...toolbar()!.querySelectorAll('button')].filter((b) => b.tabIndex === 0)
    expect(stops()).toEqual([button('undo')])

    button('undo').focus()
    button('undo').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }))
    await settle()
    expect(document.activeElement).toBe(button('redo'))
    expect(stops()).toEqual([button('redo')])

    button('redo').dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }))
    await settle()
    expect(document.activeElement).toBe(button('insert'))
    button('insert').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }),
    )
    await settle()
    expect(document.activeElement).toBe(button('undo'))
    // Reaching the toolbar with the keyboard does not make it disappear under the focus.
    expect(toolbar()).not.toBeNull()
  })

  it('follows the marks with aria-pressed, and the buttons change the text', async () => {
    const w = await mountEditor()
    const editor = await focusEditor(w)
    expect(button('bold').getAttribute('aria-pressed')).toBe('false')

    button('bold').click()
    await settle()
    expect(button('bold').getAttribute('aria-pressed')).toBe('true')
    typeText(editor, 'strong')
    expect(editor.getHTML()).toContain('<strong>strong</strong>')

    button('bold').click()
    await settle()
    expect(button('bold').getAttribute('aria-pressed')).toBe('false')

    button('bulletList').click()
    await settle()
    expect(editor.isActive('bulletList')).toBe(true)
    expect(button('bulletList').getAttribute('aria-pressed')).toBe('true')

    button('checklist').click()
    await settle()
    expect(editor.isActive('taskList')).toBe(true)

    button('heading').click()
    await settle()
    expect(editor.isActive('heading', { level: 2 })).toBe(true)
  })

  it('undoes and redoes, and says when there is nothing to do', async () => {
    const w = await mountEditor()
    const editor = await focusEditor(w)
    expect(button('undo').getAttribute('aria-disabled')).toBe('true')
    expect(button('redo').getAttribute('aria-disabled')).toBe('true')

    typeText(editor, 'hello')
    await settle()
    expect(button('undo').getAttribute('aria-disabled')).toBeNull()

    button('undo').click()
    await settle()
    expect(editor.getText()).toBe('')
    expect(button('undo').getAttribute('aria-disabled')).toBe('true')
    expect(button('redo').getAttribute('aria-disabled')).toBeNull()

    button('redo').click()
    await settle()
    expect(editor.getText()).toBe('hello')
    expect(button('redo').getAttribute('aria-disabled')).toBe('true')
  })

  it('does not undo what another client typed', async () => {
    const w = await mountEditor()
    const editor = await focusEditor(w)
    typeText(editor, 'mine')
    await settle()

    // Someone else's change arrives from the server, after the first paragraph and what was typed here.
    const remote = new Y.Doc()
    for (const update of hub.log(pageId)) Y.applyUpdate(remote, update)
    let update: Uint8Array = new Uint8Array()
    remote.on('update', (u: Uint8Array) => (update = u))
    ;((remote.getXmlFragment(FIELD).get(0) as Y.XmlElement).get(0) as Y.XmlText).insert(
      4,
      ' and theirs',
    )
    hub.store(pageId, update)
    hub.sockets[0]!.deliver(wireUpdate(pageId, update))
    await vi.waitFor(() => expect(editor.getText()).toBe('mine and theirs'))

    button('undo').click()
    await settle()
    expect(editor.getText()).toBe(' and theirs')
  })

  it('inserts a slash after a space, so the slash menu opens', async () => {
    const w = await mountEditor()
    const editor = await focusEditor(w)
    typeText(editor, 'word')

    button('insert').click()
    await settle()
    expect(editor.getText()).toBe('word /')
    expect(slashMenu()).not.toBeNull()
  })

  it('opens the slash menu at the start of an empty line without adding a space', async () => {
    const w = await mountEditor()
    const editor = await focusEditor(w)
    button('insert').click()
    await settle()
    expect(editor.getText()).toBe('/')
    expect(slashMenu()).not.toBeNull()
  })

  it('sits above the keyboard, and the page leaves room for it', async () => {
    const viewport = Object.assign(new EventTarget(), { height: 800, offsetTop: 0 })
    vi.stubGlobal('visualViewport', viewport)
    Object.defineProperty(document.documentElement, 'clientHeight', {
      value: 800,
      configurable: true,
    })
    const w = await mountEditor()
    await focusEditor(w)
    expect(document.documentElement.style.getPropertyValue('--keyboard-inset')).toBe('0px')

    viewport.height = 500
    viewport.dispatchEvent(new Event('resize'))
    expect(document.documentElement.style.getPropertyValue('--keyboard-inset')).toBe('300px')
    expect(toolbar()!.className).toContain('bottom-[var(--keyboard-inset,0px)]')
    expect(w.element.className).toContain('pb-[calc(3.5rem+var(--keyboard-inset,0px))]')
  })
})
