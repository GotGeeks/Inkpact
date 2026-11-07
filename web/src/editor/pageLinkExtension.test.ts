import { flushPromises } from '@vue/test-utils'
import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it } from 'vitest'
import type { PageNode } from '../tree/types'
import { destroyEditors, makeEditor, pressKey, typeText } from '../testing/editor'
import { openPageMenu } from './blockCommands'
import { createPageMenuState, PAGE_MENU_LISTBOX_ID } from './pageLinkExtension'

const ID_PLAN = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f10'
const ID_PLAN_B = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f11'
const ID_CAFE = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f12'
const ID_UNTITLED = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f13'
const ID_HERE = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f14'

const page = (id: string, title: string): PageNode => ({
  id,
  parentId: null,
  title,
  sortKey: 'a0',
  version: 1,
  updatedAt: '',
})

afterEach(destroyEditors)

async function typed(editor: Editor, text: string) {
  typeText(editor, text)
  await flushPromises()
}

function setup(
  pages: PageNode[] = [
    page(ID_PLAN, 'Project plan'),
    page(ID_PLAN_B, 'Plan B'),
    page(ID_CAFE, 'Café ideas'),
    page(ID_UNTITLED, ''),
    page(ID_HERE, 'This page'),
  ],
) {
  const state = createPageMenuState()
  const editor = makeEditor({
    pageLinks: {
      state,
      pages: () => pages,
      currentPageId: () => ID_HERE,
      untitled: () => 'Untitled',
    },
  })
  editor.commands.focus()
  return { state, editor }
}

const titles = (state: { items: { title: string }[] }) => state.items.map((item) => item.title)

/** The links of the page: text and address, in order. */
function links(editor: Editor) {
  const found: [string, string][] = []
  editor.state.doc.descendants((node) => {
    const mark = node.marks.find((m) => m.type.name === 'link')
    if (node.isText && mark) found.push([node.text!, mark.attrs.href as string])
  })
  return found
}

describe('the page menu', () => {
  it('opens on [[ at the start of a line, with the pages, but not the page it is on', async () => {
    const { editor, state } = setup()
    expect(state.open).toBe(false)

    await typed(editor, '[[')

    expect(state.open).toBe(true)
    expect(state.query).toBe('')
    expect(titles(state)).toEqual(['Project plan', 'Plan B', 'Café ideas', ''])
  })

  it('opens after a space, and not inside a word', async () => {
    const after = setup()
    await typed(after.editor, 'see [[')
    expect(after.state.open).toBe(true)

    for (const text of ['array[[', 'a[[', 'x]][[']) {
      const { editor, state } = setup()
      await typed(editor, text)
      expect(state.open, text).toBe(false)
    }
  })

  it('does not open in a code block or in a read-only editor', async () => {
    const code = setup()
    await typed(code.editor, '``` ')
    await typed(code.editor, '[[')
    expect(code.state.open).toBe(false)

    const readOnly = setup()
    readOnly.editor.setEditable(false)
    await typed(readOnly.editor, '[[')
    expect(readOnly.state.open).toBe(false)
  })

  it('filters while typing, across spaces, titles that start with it first', async () => {
    const { editor, state } = setup()

    await typed(editor, '[[plan')
    expect(titles(state)).toEqual(['Plan B', 'Project plan'])

    await typed(editor, ' b')
    expect(state.query).toBe('plan b')
    expect(titles(state)).toEqual(['Plan B'])
  })

  it('ignores accents and case', async () => {
    const { editor, state } = setup()
    await typed(editor, '[[CAFE')
    expect(titles(state)).toEqual(['Café ideas'])
  })

  it('moves with the arrow keys, round the ends, and names the chosen option on the editor', async () => {
    const { editor, state } = setup()
    await typed(editor, '[[')
    expect(state.selected).toBe(0)

    pressKey(editor, 'ArrowDown')
    expect(state.selected).toBe(1)
    expect(editor.view.dom.getAttribute('aria-controls')).toBe(PAGE_MENU_LISTBOX_ID)
    expect(editor.view.dom.getAttribute('aria-activedescendant')).toBe(
      `page-link-option-${ID_PLAN_B}`,
    )
    pressKey(editor, 'ArrowUp')
    pressKey(editor, 'ArrowUp')
    expect(state.selected).toBe(3)
  })

  it('on Enter makes a link to /p/<id> with the title of the page, and typing goes on outside the link', async () => {
    const { editor, state } = setup()
    await typed(editor, 'see [[plan')

    expect(pressKey(editor, 'Enter')).toBe(true)
    await flushPromises()
    await typed(editor, ' now')

    expect(state.open).toBe(false)
    expect(links(editor)).toEqual([['Plan B', `/p/${ID_PLAN_B}`]])
    expect(editor.getText()).toBe('see Plan B now')
    expect(editor.view.dom.hasAttribute('aria-controls')).toBe(false)
  })

  it('does not make what is typed next part of the link, also a letter right after it', async () => {
    const { editor } = setup()
    await typed(editor, '[[plan b')
    pressKey(editor, 'Enter')
    await flushPromises()

    await typed(editor, 's')

    expect(links(editor)).toEqual([['Plan B', `/p/${ID_PLAN_B}`]])
    expect(editor.getText()).toBe('Plan Bs')
  })

  it('on a click chooses that page', async () => {
    const { editor, state } = setup()
    await typed(editor, '[[café')

    state.choose(state.items[0]!)
    await flushPromises()

    expect(links(editor)).toEqual([['Café ideas', `/p/${ID_CAFE}`]])
  })

  it('calls a page without a title "Untitled"', async () => {
    const { editor, state } = setup()
    await typed(editor, '[[')

    state.choose(state.items.find((item) => item.id === ID_UNTITLED)!)
    await flushPromises()

    expect(links(editor)).toEqual([['Untitled', `/p/${ID_UNTITLED}`]])
  })

  it('finds a page that was made after the editor opened', async () => {
    const pages = [page(ID_PLAN, 'Project plan')]
    const state = createPageMenuState()
    const editor = makeEditor({
      pageLinks: {
        state,
        pages: () => pages,
        currentPageId: () => ID_HERE,
        untitled: () => 'Untitled',
      },
    })
    editor.commands.focus()
    pages.push(page(ID_CAFE, 'Café ideas'))

    await typed(editor, '[[caf')

    expect(titles(state)).toEqual(['Café ideas'])
  })

  it('has nothing to choose when no page matches, and then Enter is left to the text', async () => {
    const { editor, state } = setup()
    await typed(editor, '[[zzz')

    expect(state.open).toBe(true)
    expect(state.items).toEqual([])
    const blocks = editor.state.doc.childCount

    pressKey(editor, 'Enter')

    // Not taken by the menu: it is a new paragraph, as anywhere in the text, and no link was made.
    expect(editor.state.doc.childCount).toBe(blocks + 1)
    expect(links(editor)).toEqual([])
  })

  it('closes on Escape and keeps what was typed', async () => {
    const { editor, state } = setup()
    await typed(editor, '[[pla')

    pressKey(editor, 'Escape')
    await flushPromises()

    expect(state.open).toBe(false)
    expect(editor.getText()).toBe('[[pla')
    expect(links(editor)).toEqual([])
  })

  it('closes when the brackets are closed', async () => {
    const { editor, state } = setup()
    await typed(editor, '[[plan')
    expect(state.open).toBe(true)

    await typed(editor, ']')

    expect(state.open).toBe(false)
    expect(editor.getText()).toBe('[[plan]')
  })

  it('on Tab closes and lets the key move on', async () => {
    const { editor, state } = setup()
    await typed(editor, '[[')

    expect(pressKey(editor, 'Tab')).toBe(false)
    await flushPromises()

    expect(state.open).toBe(false)
  })
})

describe('opening the menu from a command', () => {
  it('types [[ at the caret, with a space first after text, and the menu opens', async () => {
    const { editor, state } = setup()
    await typed(editor, 'see')

    openPageMenu(editor)
    await flushPromises()

    expect(editor.getText()).toBe('see [[')
    expect(state.open).toBe(true)
  })

  it('types [[ without a space at the start of a line and after a space', async () => {
    const start = setup()
    openPageMenu(start.editor)
    await flushPromises()
    expect(start.editor.getText()).toBe('[[')
    expect(start.state.open).toBe(true)

    const spaced = setup()
    await typed(spaced.editor, 'see ')
    openPageMenu(spaced.editor)
    await flushPromises()
    expect(spaced.editor.getText()).toBe('see [[')
    expect(spaced.state.open).toBe(true)
  })

  it('takes away what was typed to find the command, as the slash menu does', async () => {
    const { editor, state } = setup()
    await typed(editor, 'see /link')
    const from = editor.state.selection.from - '/link'.length

    openPageMenu(editor, { from, to: editor.state.selection.from })
    await flushPromises()

    expect(editor.getText()).toBe('see [[')
    expect(state.open).toBe(true)
  })
})
