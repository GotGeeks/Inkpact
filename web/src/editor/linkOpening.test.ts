import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { destroyEditors, makeEditor } from '../testing/editor'
import { clickedLink, openLinkIn } from './linkOpening'

const ID = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f00'

afterEach(destroyEditors)

function editorWithLink(
  href: string,
  open?: (href: string, newTab: boolean) => void,
  editable = true,
) {
  const editor = makeEditor(open ? { openLink: open } : {})
  editor.commands.insertContent({
    type: 'text',
    text: 'the link',
    marks: [{ type: 'link', attrs: { href } }],
  })
  editor.setEditable(editable)
  return editor
}

/** A real click on the first link of the page, as the browser sends it: ProseMirror hands it to the plugins. */
function click(editor: Editor, init: MouseEventInit = {}, target?: Element) {
  const event = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ...init })
  ;(target ?? editor.view.dom.querySelector('a')!).dispatchEvent(event)
  return event
}

describe('a click on a link', () => {
  it('on a page being edited, only places the cursor: the link menu offers to open it', () => {
    const open = vi.fn()
    const editor = editorWithLink(`/p/${ID}`, open)

    const event = click(editor)

    expect(open).not.toHaveBeenCalled()
    expect(event.defaultPrevented).toBe(false)
  })

  it('on a page being edited, opens it in a new tab with Ctrl or Cmd', () => {
    const open = vi.fn()
    const editor = editorWithLink(`/p/${ID}`, open)

    const ctrl = click(editor, { ctrlKey: true })
    click(editor, { metaKey: true })

    expect(open.mock.calls).toEqual([
      [`/p/${ID}`, true],
      [`/p/${ID}`, true],
    ])
    expect(ctrl.defaultPrevented).toBe(true)
  })

  it('on a page that cannot be edited, opens it, and the browser does not follow it as well', () => {
    const open = vi.fn()
    const editor = editorWithLink('https://example.com/', open, false)

    const event = click(editor)

    expect(open).toHaveBeenCalledWith('https://example.com/', false)
    expect(event.defaultPrevented).toBe(true)
  })

  it('leaves Shift and Alt with a click, another button, and text that is no link alone', () => {
    const open = vi.fn()
    const editor = editorWithLink('https://example.com/', open, false)

    click(editor, { shiftKey: true })
    click(editor, { altKey: true })
    click(editor, { button: 1 })
    click(editor, {}, editor.view.dom)

    expect(open).not.toHaveBeenCalled()
  })

  it('does not open an address the editor does not allow, whoever put it in the document', () => {
    const open = vi.fn()
    const editor = editorWithLink('https://example.com/', open, false)
    editor.view.dom.querySelector('a')!.setAttribute('href', 'javascript:alert(1)')

    const event = click(editor)

    expect(open).not.toHaveBeenCalled()
    expect(event.defaultPrevented).toBe(false)
  })

  it('does nothing when the editor was given no way to open links', () => {
    const editor = editorWithLink('https://example.com/', undefined, false)

    expect(click(editor).defaultPrevented).toBe(false)
  })
})

describe('clickedLink', () => {
  it('finds the allowed link around the target inside the editor, and nothing outside it', () => {
    const root = document.createElement('div')
    root.innerHTML = `<p><a href="/p/${ID}"><strong>bold</strong></a> <a href="javascript:x">bad</a></p>`
    const outside = document.createElement('a')
    outside.href = 'https://example.com/'

    expect(clickedLink(root, root.querySelector('strong'))).toBe(`/p/${ID}`)
    expect(clickedLink(root, root.querySelectorAll('a')[1]!)).toBeNull()
    expect(clickedLink(root, outside)).toBeNull()
    expect(clickedLink(root, null)).toBeNull()
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

  it('hands a mail address to the mail program in any case, without a new tab', () => {
    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, origin: window.location.origin, assign })
    const open = vi.spyOn(window, 'open').mockReturnValue(null)

    openLinkIn(router() as never)('MAILTO:someone@example.com', false)

    expect(assign).toHaveBeenCalledWith('MAILTO:someone@example.com')
    expect(open).not.toHaveBeenCalled()
    open.mockRestore()
    vi.unstubAllGlobals()
  })
})
