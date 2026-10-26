import type { Editor } from '@tiptap/core'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import * as Y from 'yjs'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { useSyncStore } from '../stores/sync'
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

async function mountEditor(props: { readOnly?: boolean } = {}, locale: Locale = 'en') {
  wrapper = mount(PageEditor, {
    props: { pageId, readOnly: props.readOnly ?? false },
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

  it('can be typed into at once when there is plainly no server', async () => {
    hub.refusing = true
    await mountEditor()
    await vi.waitFor(() => expect(root().getAttribute('contenteditable')).toBe('true'))
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
