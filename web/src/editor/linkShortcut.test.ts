import { afterEach, describe, expect, it, vi } from 'vitest'
import { destroyEditors, makeEditor } from '../testing/editor'
import { editorTakesSearchShortcut, registerLinkShortcut } from './linkShortcutRegistry'

afterEach(destroyEditors)

/** A real Ctrl+K as the browser sends it to the editor: ProseMirror hands it to the plugins. */
function press(editor: ReturnType<typeof makeEditor>, init: KeyboardEventInit = { ctrlKey: true }) {
  const event = new KeyboardEvent('keydown', { key: 'k', bubbles: true, cancelable: true, ...init })
  editor.view.dom.dispatchEvent(event)
  return event
}

describe('the registry', () => {
  it('says the editor takes the key when the editor that registered says so, and not once it is gone', () => {
    expect(editorTakesSearchShortcut()).toBe(false)

    let wants = true
    const stop = registerLinkShortcut(() => wants)
    expect(editorTakesSearchShortcut()).toBe(true)
    wants = false
    expect(editorTakesSearchShortcut()).toBe(false)
    wants = true

    stop()
    expect(editorTakesSearchShortcut()).toBe(false)
  })

  it('is not undone by an editor that was replaced: only the one that registered takes itself away', () => {
    const first = registerLinkShortcut(() => true)
    const stopSecond = registerLinkShortcut(() => false)

    first()
    expect(editorTakesSearchShortcut()).toBe(false)
    stopSecond()
    expect(editorTakesSearchShortcut()).toBe(false)
  })
})

describe('Ctrl+K in the editor', () => {
  it('opens the picker and takes the key (the browser does not use it either) when there is something to link', () => {
    const open = vi.fn(() => true)
    const editor = makeEditor({ linkShortcut: { open } })

    const event = press(editor)

    expect(open).toHaveBeenCalledOnce()
    expect(event.defaultPrevented).toBe(true)
  })

  it('works with Cmd on a Mac, and with neither Alt nor Shift', () => {
    const open = vi.fn(() => true)
    const editor = makeEditor({ linkShortcut: { open } })

    press(editor, { metaKey: true })
    press(editor, { ctrlKey: true, altKey: true })
    press(editor, { ctrlKey: true, shiftKey: true })
    press(editor, {})

    expect(open).toHaveBeenCalledOnce()
  })

  it('leaves the key alone when there is nothing to link, so that search opens as everywhere', () => {
    const editor = makeEditor({ linkShortcut: { open: () => false } })

    const event = press(editor)

    expect(event.defaultPrevented).toBe(false)
  })

  it('does nothing in an editor that was not told how to open the picker', () => {
    const editor = makeEditor()

    expect(press(editor).defaultPrevented).toBe(false)
  })
})
