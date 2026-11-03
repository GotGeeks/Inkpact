import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { destroyEditors, makeEditor } from '../testing/editor'
import { openLinkIn } from './linkOpening'

const ID = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f00'

afterEach(destroyEditors)

function editorWithLink(href: string, open: (href: string, newTab: boolean) => void) {
  const editor = makeEditor({ openLink: open })
  editor.commands.insertContent({
    type: 'text',
    text: 'the link',
    marks: [{ type: 'link', attrs: { href } }],
  })
  return editor
}

/** A click on the first link of the page, as ProseMirror hands it to the plugins. */
function click(editor: Editor, init: Partial<MouseEvent> = {}, target?: Element) {
  const anchor = target ?? editor.view.dom.querySelector('a')!
  const event = {
    button: 0,
    ctrlKey: false,
    metaKey: false,
    shiftKey: false,
    target: anchor,
    preventDefault: vi.fn(),
    ...init,
  } as unknown as MouseEvent
  const handled = editor.view.someProp('handleClick', (f) => f(editor.view, 1, event)) ?? false
  return { handled, event }
}

describe('a click on a link', () => {
  it('opens a page link', () => {
    const open = vi.fn()
    const editor = editorWithLink(`/p/${ID}`, open)

    const { handled, event } = click(editor)

    expect(handled).toBe(true)
    expect(event.preventDefault).toHaveBeenCalled()
    expect(open).toHaveBeenCalledWith(`/p/${ID}`, false)
  })

  it('opens a web link, and asks for a new tab when Ctrl, Cmd or Shift is held', () => {
    const open = vi.fn()
    const editor = editorWithLink('https://example.com/', open)

    click(editor)
    click(editor, { ctrlKey: true })
    click(editor, { metaKey: true })
    click(editor, { shiftKey: true })

    expect(open.mock.calls.map((call) => call[1])).toEqual([false, true, true, true])
  })

  it('does nothing for a button other than the main one, or for text that is no link', () => {
    const open = vi.fn()
    const editor = editorWithLink('https://example.com/', open)

    expect(click(editor, { button: 1 }).handled).toBe(false)
    expect(click(editor, {}, editor.view.dom).handled).toBe(false)
    expect(open).not.toHaveBeenCalled()
  })

  it('does not open an address the editor does not allow, whoever put it in the document', () => {
    const open = vi.fn()
    const editor = editorWithLink('https://example.com/', open)
    const anchor = editor.view.dom.querySelector('a')!
    anchor.setAttribute('href', 'javascript:alert(1)')

    expect(click(editor).handled).toBe(false)
    expect(open).not.toHaveBeenCalled()
  })

  it('does nothing when the editor was given no way to open links', () => {
    const editor = makeEditor()
    editor.commands.insertContent({
      type: 'text',
      text: 'the link',
      marks: [{ type: 'link', attrs: { href: 'https://example.com/' } }],
    })

    expect(click(editor).handled).toBe(false)
  })
})

describe('openLinkIn', () => {
  const router = () => ({
    push: vi.fn().mockResolvedValue(undefined),
    resolve: vi.fn((route: { params: { pageId: string } }) => ({
      href: `/p/${route.params.pageId}`,
    })),
  })

  it('goes to a page through the router in the same tab', () => {
    const r = router()
    const open = vi.spyOn(window, 'open').mockReturnValue(null)

    openLinkIn(r as never)(`/p/${ID}`, false)

    expect(r.push).toHaveBeenCalledWith({ name: 'page', params: { pageId: ID } })
    expect(open).not.toHaveBeenCalled()
    open.mockRestore()
  })

  it('opens a page in a new tab on request, and a page copied with its full address too', () => {
    const r = router()
    const open = vi.spyOn(window, 'open').mockReturnValue(null)

    openLinkIn(r as never)(`/p/${ID}`, true)
    openLinkIn(r as never)(`${window.location.origin}/p/${ID}`, false)

    expect(open).toHaveBeenCalledWith(`/p/${ID}`, '_blank', 'noopener,noreferrer')
    expect(r.push).toHaveBeenCalledTimes(1)
    open.mockRestore()
  })

  it('opens another site in a new tab that cannot reach this one', () => {
    const r = router()
    const open = vi.spyOn(window, 'open').mockReturnValue(null)

    openLinkIn(r as never)('https://example.com/', false)

    expect(open).toHaveBeenCalledWith('https://example.com/', '_blank', 'noopener,noreferrer')
    expect(r.push).not.toHaveBeenCalled()
    open.mockRestore()
  })
})
