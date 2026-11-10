import type { Editor } from '@tiptap/core'
import { ySyncPluginKey } from '@tiptap/y-tiptap'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import * as Y from 'yjs'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { OfflineDocument } from '../offline/offlineDocument'
import type { CachedPage, OfflineRepository } from '../offline/types'
import { useSyncStore } from '../stores/sync'
import { useTreeStore } from '../stores/tree'
import { typeText } from '../testing/editor'
import { editorTakesSearchShortcut } from './linkShortcutRegistry'
import { FakeSyncHub } from '../testing/fakeSyncHub'
import PageEditor from './PageEditor.vue'
import { applyFirstParagraph, FIELD } from './firstParagraph'

const pageId = '0a1b2c3d-0000-4000-8000-000000000001'

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

async function mountEditor(
  props: { readOnly?: boolean; document?: OfflineDocument } = {},
  locale: Locale = 'en',
) {
  wrapper = mount(PageEditor, {
    props: { pageId, readOnly: props.readOnly ?? false, document: props.document },
    attachTo: document.body,
    global: { plugins: [createAppI18n(locale)] },
  })
  await flushPromises()
  return wrapper
}

/** The ProseMirror root, and the TipTap editor behind it. */
function root(): HTMLElement {
  return document.querySelector('.ProseMirror') as HTMLElement
}
function editorOf(w: VueWrapper): Editor {
  return (w.findComponent({ name: 'EditorContent' }).props('editor') as Editor | undefined)!
}

/** What the server holds: the first paragraph, then text typed into it by someone else. */
function pageWithText(text: string, attributes?: Record<string, unknown>): Uint8Array {
  const doc = new Y.Doc()
  applyFirstParagraph(doc)
  const paragraph = doc.getXmlFragment(FIELD).get(0) as Y.XmlElement
  ;(paragraph.get(0) as Y.XmlText).insert(0, text, attributes)
  return Y.encodeStateAsUpdate(doc)
}

/** Types the way a keyboard does: each character goes through ProseMirror's text input handlers, so input rules run. */
function type(editor: Editor, text: string) {
  const { view } = editor
  for (const char of text) {
    const { from, to } = view.state.selection
    const handled = view.someProp('handleTextInput', (handler) =>
      handler(view, from, to, char, () => view.state.tr),
    )
    if (!handled) view.dispatch(view.state.tr.insertText(char, from, to))
  }
}

describe('PageEditor', () => {
  it('shows what the server has, and what others type afterwards', async () => {
    hub.store(pageId, pageWithText('hello'))
    const w = await mountEditor()
    expect(root().textContent).toBe('hello')

    const remote = new Y.Doc()
    Y.applyUpdate(remote, hub.log(pageId)[0]!)
    let update: Uint8Array = new Uint8Array()
    remote.on('update', (u: Uint8Array) => (update = u))
    ;((remote.getXmlFragment(FIELD).get(0) as Y.XmlElement).get(0) as Y.XmlText).insert(5, ' world')
    hub.store(pageId, update)
    hub.sockets[0]!.deliver(wireUpdate(pageId, update))

    await vi.waitFor(() => expect(root().textContent).toBe('hello world'))
    expect(editorOf(w).getJSON().content).toHaveLength(1)
  })

  it('gives a new page its first paragraph, once', async () => {
    await mountEditor()
    expect(hub.log(pageId)).toHaveLength(1)

    wrapper!.unmount()
    wrapper = null
    useSyncStore().destroy()
    await mountEditor()
    // A second visit finds it on the server and adds nothing.
    expect(hub.log(pageId)).toHaveLength(1)
  })

  it('cannot be typed into before the page has arrived, and can once it has', async () => {
    hub.holding = true
    await mountEditor()
    expect(root().getAttribute('contenteditable')).toBe('false')

    hub.release()
    await flushPromises()
    expect(root().getAttribute('contenteditable')).toBe('true')
  })

  it('takes the focus it was asked for before the page arrived as soon as it can be typed into, and not long after', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    hub.holding = true
    const w = await mountEditor()
    ;(w.vm as unknown as { focus: () => void }).focus()
    expect(document.activeElement).not.toBe(root())

    hub.release()
    await flushPromises()

    expect(root().getAttribute('contenteditable')).toBe('true')
    // TipTap focuses on the next animation frame.
    await vi.waitFor(() => expect(document.activeElement).toBe(root()))
    vi.useRealTimers()
  })

  it('does not take the focus when the person pressed a key or clicked after asking for it', async () => {
    for (const went of [
      () => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'a', bubbles: true })),
      () => document.body.dispatchEvent(new Event('pointerdown', { bubbles: true })),
    ]) {
      hub.holding = true
      const w = await mountEditor()
      ;(w.vm as unknown as { focus: () => void }).focus()
      went()

      hub.release()
      await flushPromises()
      await new Promise((resolve) => setTimeout(resolve, 100))

      expect(root().getAttribute('contenteditable')).toBe('true')
      expect(document.activeElement).not.toBe(root())
      wrapper!.unmount()
      wrapper = null
      useSyncStore().destroy()
      document.body.innerHTML = ''
    }
  })

  it('does not take a focus that was asked for long ago', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    hub.holding = true
    const w = await mountEditor()
    ;(w.vm as unknown as { focus: () => void }).focus()
    vi.setSystemTime(Date.now() + 6000)

    hub.release()
    await flushPromises()

    expect(root().getAttribute('contenteditable')).toBe('true')
    await new Promise((resolve) => setTimeout(resolve, 100))
    expect(document.activeElement).not.toBe(root())
    vi.useRealTimers()
  })

  it('takes the focus at once when it can already be typed into', async () => {
    const w = await mountEditor()
    expect(root().getAttribute('contenteditable')).toBe('true')

    ;(w.vm as unknown as { focus: () => void }).focus()

    await vi.waitFor(() => expect(document.activeElement).toBe(root()))
  })

  it('does not make an uncached page editable when the server never sent its content', async () => {
    hub.refusing = true
    await mountEditor()
    await vi.waitFor(() => expect(useSyncStore().connection).toBe('offline'))
    expect(root().getAttribute('contenteditable')).toBe('false')
  })

  it('opens a fully hydrated cached document for editing when the server is offline', async () => {
    const metadata: CachedPage = {
      userId: 'user',
      workspaceId: 'workspace',
      pageId,
      title: 'Cached',
      inTrash: false,
      ownTrash: false,
      canEdit: true,
      ready: true,
      lastOpened: 1,
    }
    const repository: OfflineRepository = {
      findPage: async () => metadata,
      readSnapshots: async () => [
        { ...metadata, writerId: 'old', update: pageWithText('cached'), unconfirmed: 0 },
      ],
      saveSnapshot: async () => {},
      saveRecovery: async () => {},
      listRecoveries: async () => [],
      deleteRecovery: async () => {},
      cleanup: async () => 0,
    }
    const document = await OfflineDocument.open(repository, metadata, false, () => {})
    hub.refusing = true
    await mountEditor({ document })
    await vi.waitFor(() => expect(useSyncStore().connection).toBe('offline'))
    expect(root().textContent).toBe('cached')
    expect(root().getAttribute('contenteditable')).toBe('true')
  })

  it('is read-only for a viewer', async () => {
    await mountEditor({ readOnly: true })
    expect(root().getAttribute('contenteditable')).toBe('false')
  })

  it('becomes read-only when the server says the person may not change the page', async () => {
    hub.readOnly.add(pageId)
    const w = await mountEditor()
    type(editorOf(w), 'x')
    await flushPromises()

    await vi.waitFor(() => expect(root().getAttribute('contenteditable')).toBe('false'))
  })

  it('is read-only, with a message, when the page is too large to save', async () => {
    const w = await mountEditor()
    editorOf(w).commands.insertContent('x'.repeat(2.2 * 1024 * 1024))
    await flushPromises()

    expect(root().getAttribute('contenteditable')).toBe('false')
  })

  it('stops syncing a page it does not understand, and says to reload', async () => {
    const doc = new Y.Doc()
    applyFirstParagraph(doc)
    // Something a newer version of the app made, which this schema does not have.
    doc.getXmlFragment(FIELD).insert(1, [new Y.XmlElement('futureBlock')])
    hub.store(pageId, Y.encodeStateAsUpdate(doc))

    const w = await mountEditor()

    await vi.waitFor(() => expect(w.find('[role="alert"]').exists()).toBe(true))
    expect(w.get('[role="alert"]').text()).toContain('newer version of Inkpact')
    expect(root().getAttribute('contenteditable')).toBe('false')
    // The block it does not know is still in the page, here and on the server: nothing was written for it.
    const local = (ySyncPluginKey.getState(editorOf(w).state) as { binding: { doc: Y.Doc } })
      .binding.doc
    expect(
      local
        .getXmlFragment(FIELD)
        .toArray()
        .map((node) => (node as Y.XmlElement).nodeName),
    ).toEqual(['paragraph', 'futureBlock'])
    expect(hub.log(pageId)).toHaveLength(1)
  })

  it('shows a link another client wrote with a script address without that address', async () => {
    hub.store(
      pageId,
      pageWithText('click me', {
        link: { href: 'javascript:window.pwned = 1', target: null, rel: null, class: null },
      }),
    )
    await mountEditor()

    const link = root().querySelector('a')
    expect(link?.textContent).toBe('click me')
    expect(link?.getAttribute('href') ?? '').not.toMatch(/^javascript:/i)
  })

  it('shows a link to a web address with its address', async () => {
    hub.store(
      pageId,
      pageWithText('docs', {
        link: { href: 'https://example.com/a', target: null, rel: null, class: null },
      }),
    )
    await mountEditor()

    expect(root().querySelector('a')!.getAttribute('href')).toBe('https://example.com/a')
  })

  it('makes a link that opens in a new tab without telling the site where it came from', async () => {
    const w = await mountEditor()
    type(editorOf(w), 'https://example.com/a ')

    const link = root().querySelector('a')!
    expect(link.getAttribute('rel')).toBe('noopener noreferrer nofollow')
    expect(link.getAttribute('target')).toBe('_blank')
  })

  it.each([
    ['# ', 'h1'],
    ['###### ', 'h6'],
    ['- ', 'ul li'],
    ['1. ', 'ol li'],
    ['[ ] ', 'ul[data-type="taskList"] li'],
    ['> ', 'blockquote'],
    ['``` ', 'pre code'],
  ])('turns "%s" at the start of a line into a block (%s)', async (typed, selector) => {
    const w = await mountEditor()
    type(editorOf(w), typed)
    expect(root().querySelector(selector), selector).not.toBeNull()
  })

  it('turns a web address followed by a space into a link', async () => {
    const w = await mountEditor()
    type(editorOf(w), 'see https://example.com/page ')
    expect(root().querySelector('a')?.getAttribute('href')).toBe('https://example.com/page')
  })

  it('does not turn a script address into a link', async () => {
    const w = await mountEditor()
    type(editorOf(w), 'javascript:alert(1) ')
    expect(root().querySelector('a')).toBeNull()
  })

  it('writes a checklist box that says whether it is done, in the language of the page', async () => {
    const w = await mountEditor({}, 'nl')
    type(editorOf(w), '[ ] ')
    expect(root().querySelector('input[type="checkbox"]')?.getAttribute('aria-label')).toBe(
      'Niet klaar',
    )
  })

  it('has a name for screen readers and a placeholder, in the language of the page', async () => {
    await mountEditor({}, 'nl')
    expect(root().getAttribute('role')).toBe('textbox')
    expect(root().getAttribute('aria-multiline')).toBe('true')
    expect(root().getAttribute('aria-label')).toBe('Inhoud van de pagina')
    expect(root().querySelector('p')?.getAttribute('data-placeholder')).toBe('Begin met schrijven…')
  })

  it('serialises a table without a style attribute, and injects no style element', async () => {
    const w = await mountEditor()
    editorOf(w).commands.insertTable({ rows: 2, cols: 2, withHeaderRow: true })
    expect(root().querySelector('table')).not.toBeNull()
    // What the editor serialises (a copy, a cut) has no style attribute: setting one is blocked by the policy. The live
    // table sets its minimum width through the style object, which the policy allows.
    expect(editorOf(w).getHTML()).not.toContain('style=')
    expect(document.querySelectorAll('style')).toHaveLength(0)
  })

  it('leaves the page when it goes, and keeps nothing running', async () => {
    await mountEditor()
    const sync = useSyncStore()
    expect(sync.pages[pageId]).toBeDefined()

    wrapper!.unmount()
    wrapper = null
    // The last update is confirmed by a pong first (a dead connection would swallow it), which takes a round trip.
    await flushPromises()

    expect(sync.pages[pageId]).toBeUndefined()
    expect(hub.sent(1, pageId)).toHaveLength(1)
  })
})

/** An update frame from the server, as bytes. */
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

describe('a large page', () => {
  it.each([
    ['en', 'This page is large (6 MB), so it may be slow to open and to edit.'],
    ['nl', 'Deze pagina is groot (6 MB), dus openen en bewerken kan traag zijn.'],
  ] as const)('says so once its state is over 5 MiB, in %s', async (locale, message) => {
    hub.store(pageId, pageWithText('x'.repeat(6 * 1000 * 1000)))

    await mountEditor({}, locale)

    const notice = document.querySelector('[data-testid="large-page"]')
    expect(notice?.getAttribute('role')).toBe('status')
    expect(notice?.textContent?.trim()).toBe(message)
  })

  it('says nothing for a page under that, or before the page has been in step with the server', async () => {
    hub.store(pageId, pageWithText('x'.repeat(4 * 1000 * 1000)))

    await mountEditor()

    expect(document.querySelector('[data-testid="large-page"]')).toBeNull()
  })

  it('measures the page once: text typed afterwards does not make it large', async () => {
    hub.store(pageId, pageWithText('hello'))
    const w = await mountEditor()

    type(editorOf(w), 'x'.repeat(1000))
    await flushPromises()

    expect(document.querySelector('[data-testid="large-page"]')).toBeNull()
  })
})

describe('making links', () => {
  const ID_TARGET = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f40'
  const ID_OTHER = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f41'

  const picker = () => document.querySelector<HTMLElement>('[data-testid="link-picker"]')
  const pickerOption = (text: string) =>
    [...document.querySelectorAll<HTMLElement>('[role="option"]')].find((option) =>
      option.textContent!.includes(text),
    )!
  const pageMenu = () => document.querySelector('[data-testid="page-link-menu"]')

  /** TipTap tells Vue about a new editor state after two animation frames. */
  async function settle() {
    await flushPromises()
    await new Promise<void>((resolve) =>
      requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
    )
    await flushPromises()
  }

  async function mountWithPages(readOnly = false) {
    useTreeStore().pages = [
      {
        id: ID_TARGET,
        parentId: null,
        title: 'The target',
        sortKey: 'a0',
        version: 1,
        updatedAt: '',
      },
      {
        id: ID_OTHER,
        parentId: null,
        title: 'Other page',
        sortKey: 'a1',
        version: 1,
        updatedAt: '',
      },
    ]
    const w = await mountEditor({ readOnly })
    const editor = editorOf(w)
    // jsdom only lets an element with a tab index take the focus; a browser does so for what can be edited.
    editor.view.dom.tabIndex = 0
    editor.commands.insertContent('hello world')
    editor.view.dom.focus()
    await settle()
    return editor
  }

  function ctrlK(editor: Editor) {
    const event = new KeyboardEvent('keydown', {
      key: 'k',
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    })
    editor.view.dom.dispatchEvent(event)
    return event
  }

  const links = (editor: Editor) => {
    const found: [string, string][] = []
    editor.state.doc.descendants((node) => {
      const mark = node.marks.find((m) => m.type.name === 'link')
      if (node.isText && mark) found.push([node.text!, mark.attrs.href as string])
    })
    return found
  }

  it('links the selected text to a page with Ctrl+K, keeping its text, and the caret goes after the link', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })

    const event = ctrlK(editor)
    await settle()

    expect(event.defaultPrevented).toBe(true)
    expect(picker()).not.toBeNull()
    pickerOption('The target').click()
    await settle()

    expect(picker()).toBeNull()
    expect(links(editor)).toEqual([['world', `/p/${ID_TARGET}`]])
    expect(editor.getText()).toBe('hello world')
    expect(editor.state.selection.empty).toBe(true)
    expect(editor.state.selection.from).toBe(12)
    expect(document.activeElement).toBe(editor.view.dom)
    // What is typed next is not part of the link.
    typeText(editor, 's')
    expect(links(editor)).toEqual([['world', `/p/${ID_TARGET}`]])
  })

  it('links the selected text to a web address that is typed', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 1, to: 6 })
    ctrlK(editor)
    await settle()

    const field = document.querySelector<HTMLInputElement>('[data-testid="link-picker-input"]')!
    field.value = 'https://example.com/'
    field.dispatchEvent(new Event('input', { bubbles: true }))
    await flushPromises()
    field.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }),
    )
    await settle()

    expect(links(editor)).toEqual([['hello', 'https://example.com/']])
  })

  it('on Escape leaves the text as it was, selected again, with the focus back in the editor', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    ctrlK(editor)
    await settle()

    document
      .querySelector('[role="dialog"]')!
      .dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
      )
    await settle()

    expect(picker()).toBeNull()
    expect(links(editor)).toEqual([])
    expect(editor.state.selection.from).toBe(7)
    expect(editor.state.selection.to).toBe(12)
    expect(document.activeElement).toBe(editor.view.dom)
  })

  it('does not take Ctrl+K when nothing is selected and the caret is not in a link, so that search opens', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection(3)

    const event = ctrlK(editor)
    await settle()

    expect(event.defaultPrevented).toBe(false)
    expect(picker()).toBeNull()
    expect(editorTakesSearchShortcut()).toBe(false)
  })

  it('says to the app that the editor takes Ctrl+K only while it has the focus and there is something to link', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    expect(editorTakesSearchShortcut()).toBe(true)

    editor.view.dom.blur()
    expect(editorTakesSearchShortcut()).toBe(false)
    editor.view.dom.focus()
    editor.commands.setTextSelection(3)
    expect(editorTakesSearchShortcut()).toBe(false)
  })

  it('does not take Ctrl+K on a page that cannot be edited', async () => {
    const editor = await mountWithPages(true)
    editor.commands.setTextSelection({ from: 1, to: 3 })

    const event = ctrlK(editor)
    await settle()

    expect(event.defaultPrevented).toBe(false)
    expect(picker()).toBeNull()
    expect(editorTakesSearchShortcut()).toBe(false)
  })

  it('with the caret in a link, Ctrl+K takes the whole link, to point it somewhere else', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    editor.commands.setLink({ href: `/p/${ID_TARGET}` })
    editor.commands.setTextSelection(9)
    await settle()

    ctrlK(editor)
    await settle()
    expect(document.querySelector('[data-testid="link-picker-remove"]')).not.toBeNull()
    pickerOption('Other page').click()
    await settle()

    expect(links(editor)).toEqual([['world', `/p/${ID_OTHER}`]])
  })

  it('takes the link away with the picker, keeping the text', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    editor.commands.setLink({ href: `/p/${ID_TARGET}` })
    editor.commands.setTextSelection(9)
    await settle()

    ctrlK(editor)
    await settle()
    document.querySelector<HTMLButtonElement>('[data-testid="link-picker-remove"]')!.click()
    await settle()

    expect(links(editor)).toEqual([])
    expect(editor.getText()).toBe('hello world')
  })

  it('has the link menu change a link, and remove it', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    editor.commands.setLink({ href: `/p/${ID_TARGET}` })
    editor.commands.setTextSelection(9)
    await settle()

    expect(document.querySelector('[data-testid="link-menu"]')).not.toBeNull()
    document.querySelector<HTMLButtonElement>('[data-testid="link-change"]')!.click()
    await settle()
    expect(picker()).not.toBeNull()
    pickerOption('Other page').click()
    await settle()
    expect(links(editor)).toEqual([['world', `/p/${ID_OTHER}`]])

    editor.commands.setTextSelection(9)
    await settle()
    document.querySelector<HTMLButtonElement>('[data-testid="link-remove"]')!.click()
    await settle()
    expect(links(editor)).toEqual([])
    expect(editor.getText()).toBe('hello world')
  })

  it('lets [[ link to a page, in the page the editor is on', async () => {
    const editor = await mountWithPages()
    editor.commands.focus('end')

    typeText(editor, ' see [[oth')
    await settle()
    expect(pageMenu()).not.toBeNull()
    expect(document.querySelector('[role="option"]')!.textContent).toContain('Other page')
    editor.view.dom.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }),
    )
    await settle()

    expect(pageMenu()).toBeNull()
    expect(links(editor)).toEqual([['Other page', `/p/${ID_OTHER}`]])
  })

  /** Someone else changes the first paragraph's text, through the server, while this tab has the page open. */
  async function remoteEdit(editor: Editor, change: (text: Y.XmlText) => void) {
    const local = (ySyncPluginKey.getState(editor.state) as { binding: { doc: Y.Doc } }).binding.doc
    const remote = new Y.Doc()
    Y.applyUpdate(remote, Y.encodeStateAsUpdate(local))
    let update: Uint8Array = new Uint8Array()
    remote.on('update', (u: Uint8Array) => (update = u))
    const paragraph = remote.getXmlFragment(FIELD).get(0) as Y.XmlElement
    change(paragraph.get(0) as Y.XmlText)
    hub.sockets[0]!.deliver(wireUpdate(pageId, update))
    await settle()
  }

  it('puts the link on the text it was opened for, when someone else changes the paragraph while the picker is open', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    ctrlK(editor)
    await settle()

    await remoteEdit(editor, (text) => text.insert(0, 'Oh, '))
    expect(editor.getText()).toBe('Oh, hello world')
    pickerOption('The target').click()
    await settle()

    expect(links(editor)).toEqual([['world', `/p/${ID_TARGET}`]])
  })

  it('makes no link when someone else deleted the text while the picker was open, and gives the focus back', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    ctrlK(editor)
    await settle()

    await remoteEdit(editor, (text) => text.delete(5, 6))
    expect(editor.getText()).toBe('hello')
    pickerOption('The target').click()
    await settle()

    expect(picker()).toBeNull()
    expect(links(editor)).toEqual([])
    expect(document.activeElement).toBe(editor.view.dom)
  })

  it('makes no link when the page stops being editable while the picker is open', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    ctrlK(editor)
    await settle()
    expect(picker()).not.toBeNull()

    await wrapper!.setProps({ readOnly: true })
    await settle()

    expect(picker()).toBeNull()
    expect(links(editor)).toEqual([])
  })

  it('changes and removes the whole link when only a part of it is selected, never splitting it', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    editor.commands.setLink({ href: `/p/${ID_TARGET}` })
    editor.commands.setTextSelection({ from: 8, to: 10 })
    await settle()

    document.querySelector<HTMLButtonElement>('[data-testid="link-change"]')!.click()
    await settle()
    pickerOption('Other page').click()
    await settle()
    expect(links(editor)).toEqual([['world', `/p/${ID_OTHER}`]])

    editor.commands.setTextSelection({ from: 8, to: 10 })
    await settle()
    document.querySelector<HTMLButtonElement>('[data-testid="link-remove"]')!.click()
    await settle()
    expect(links(editor)).toEqual([])
    // The selection is as it was.
    expect([editor.state.selection.from, editor.state.selection.to]).toEqual([8, 10])
  })

  it('with a part of a link selected, Ctrl+K changes the whole link', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection({ from: 7, to: 12 })
    editor.commands.setLink({ href: `/p/${ID_TARGET}` })
    editor.commands.setTextSelection({ from: 8, to: 10 })
    await settle()

    ctrlK(editor)
    await settle()
    pickerOption('Other page').click()
    await settle()

    expect(links(editor)).toEqual([['world', `/p/${ID_OTHER}`]])
  })

  it('leaves Ctrl+K to search in code, where a link cannot go, and the app and the editor agree on it', async () => {
    const editor = await mountWithPages()
    editor.commands.setTextSelection(1)
    editor.commands.setCodeBlock()
    editor.commands.setTextSelection({ from: 1, to: 6 })
    await settle()

    expect(editorTakesSearchShortcut()).toBe(false)
    const event = ctrlK(editor)
    await settle()

    expect(event.defaultPrevented).toBe(false)
    expect(picker()).toBeNull()
  })
})
