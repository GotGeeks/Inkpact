import { flushPromises } from '@vue/test-utils'
import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it } from 'vitest'
import { createAppI18n } from '../i18n'
import { destroyEditors, makeEditor, pressKey, typeText } from '../testing/editor'
import { createSlashState, SLASH_LISTBOX_ID } from './slashExtension'

/** Types, and waits for the menu: the list of items arrives a moment after the keystroke. */
async function typed(editor: Editor, text: string) {
  typeText(editor, text)
  await flushPromises()
}

afterEach(destroyEditors)

const english = createAppI18n('en').global.t as (key: string) => string
const dutch = createAppI18n('nl').global.t as (key: string) => string

function setup(translate = english) {
  const state = createSlashState()
  const editor = makeEditor({ slash: { state, translate } })
  editor.commands.focus()
  return { state, editor }
}

const labels = (state: { items: { id: string }[] }) => state.items.map((item) => item.id)

describe('the slash menu', () => {
  it('opens on a slash at the start of a line, with every block', async () => {
    const { editor, state } = setup()
    expect(state.open).toBe(false)
    await typed(editor, '/')
    expect(state.open).toBe(true)
    expect(state.query).toBe('')
    expect(state.items).toHaveLength(11)
  })

  it('opens after a space', async () => {
    const { editor, state } = setup()
    await typed(editor, 'some text /')
    expect(state.open).toBe(true)
  })

  it('does not open inside a word or an address', async () => {
    for (const text of ['and/or', 'see https://example.com', 'a//b', '1/2']) {
      const { editor, state } = setup()
      await typed(editor, text)
      expect(state.open, text).toBe(false)
    }
  })

  it('does not open in a code block', async () => {
    const { editor, state } = setup()
    await typed(editor, '``` ')
    expect(editor.isActive('codeBlock')).toBe(true)
    await typed(editor, '/')
    expect(state.open).toBe(false)
  })

  it('does not open in a read-only editor', async () => {
    const { editor, state } = setup()
    editor.setEditable(false)
    await typed(editor, '/')
    expect(state.open).toBe(false)
  })

  it('filters while typing, and starts at the top again', async () => {
    const { editor, state } = setup()
    await typed(editor, '/')
    pressKey(editor, 'ArrowDown')
    expect(state.selected).toBe(1)

    await typed(editor, 'hea')
    expect(state.query).toBe('hea')
    expect(labels(state)).toEqual(['heading1', 'heading2', 'heading3'])
    expect(state.selected).toBe(0)
  })

  it('filters in Dutch', async () => {
    const { editor, state } = setup(dutch)
    await typed(editor, '/lijst')
    expect(labels(state)).toEqual(['bulletList', 'numberedList'])
  })

  it('moves through the list with the arrow keys, around the ends, and names the option for a screen reader', async () => {
    const { editor, state } = setup()
    await typed(editor, '/hea')
    const root = editor.view.dom
    expect(root.getAttribute('aria-controls')).toBe(SLASH_LISTBOX_ID)
    expect(root.getAttribute('aria-activedescendant')).toBe('slash-option-heading1')

    expect(pressKey(editor, 'ArrowDown')).toBe(true)
    expect(root.getAttribute('aria-activedescendant')).toBe('slash-option-heading2')
    pressKey(editor, 'ArrowDown')
    pressKey(editor, 'ArrowDown')
    expect(state.selected).toBe(0)
    expect(pressKey(editor, 'ArrowUp')).toBe(true)
    expect(state.selected).toBe(2)
    expect(root.getAttribute('aria-activedescendant')).toBe('slash-option-heading3')
  })

  it('makes the chosen block with Enter, and the menu is gone', async () => {
    const { editor, state } = setup()
    await typed(editor, '/hea')
    pressKey(editor, 'ArrowDown')
    expect(pressKey(editor, 'Enter')).toBe(true)

    expect(editor.getJSON().content![0]).toEqual({ type: 'heading', attrs: { level: 2 } })
    expect(state.open).toBe(false)
    expect(editor.view.dom.hasAttribute('aria-controls')).toBe(false)
    expect(editor.view.dom.hasAttribute('aria-activedescendant')).toBe(false)
  })

  it('makes the block that is chosen with the pointer', async () => {
    const { editor, state } = setup()
    await typed(editor, '/quo')
    state.choose(state.items[0]!)
    expect(editor.getJSON().content![0]!.type).toBe('blockquote')
  })

  it('closes on Escape and leaves what was typed', async () => {
    const { editor, state } = setup()
    await typed(editor, '/hea')
    expect(pressKey(editor, 'Escape')).toBe(true)
    expect(state.open).toBe(false)
    expect(editor.getText()).toBe('/hea')
  })

  it('closes on Tab and lets the key through', async () => {
    const { editor, state } = setup()
    await typed(editor, '/')
    expect(pressKey(editor, 'Tab')).toBe(false)
    expect(state.open).toBe(false)
  })

  it('closes when the slash is deleted', async () => {
    const { editor, state } = setup()
    await typed(editor, '/he')
    expect(state.open).toBe(true)
    editor.commands.deleteRange({ from: 1, to: 4 })
    await flushPromises()
    expect(state.open).toBe(false)
  })

  it('closes on a space, and what was typed stays text', async () => {
    const { editor, state } = setup()
    await typed(editor, '/zzz')
    expect(state.open).toBe(true)
    expect(state.items).toEqual([])

    await typed(editor, ' ')
    expect(state.open).toBe(false)
    expect(editor.getText()).toBe('/zzz ')
  })

  it('leaves Enter to the editor when nothing matches', async () => {
    const { editor, state } = setup()
    await typed(editor, '/zzz')
    pressKey(editor, 'Enter')
    await flushPromises()
    expect(state.open).toBe(false)
    expect(editor.getJSON().content).toHaveLength(2)
  })
})
