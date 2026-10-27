import type { JSONContent } from '@tiptap/core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../i18n'
import { destroyEditors, makeEditor, typeText } from '../testing/editor'
import { blockCommands, filterCommands } from './blockCommands'

afterEach(() => {
  destroyEditors()
  vi.restoreAllMocks()
})

const english = createAppI18n('en').global.t as (key: string) => string
const dutch = createAppI18n('nl').global.t as (key: string) => string
const ids = (query: string, t = english) => filterCommands(query, t).map((command) => command.id)

function command(id: string) {
  return blockCommands.find((candidate) => candidate.id === id)!
}

describe('block commands', () => {
  // What typing the Markdown shortcut makes; a block from the menu must be the very same node.
  const shortcuts: Record<string, string> = {
    heading1: '# ',
    heading2: '## ',
    heading3: '### ',
    bulletList: '- ',
    numberedList: '1. ',
    checklist: '[ ] ',
    quote: '> ',
    codeBlock: '``` ',
  }

  it.each(Object.entries(shortcuts))('%s makes what "%s" makes', (id, typed) => {
    const viaShortcut = makeEditor()
    viaShortcut.commands.focus()
    typeText(viaShortcut, typed)

    const viaMenu = makeEditor()
    viaMenu.commands.focus()
    command(id).run(viaMenu)

    expect(viaMenu.getJSON()).toEqual(viaShortcut.getJSON())
  })

  it('removes what was typed to find the block', () => {
    const editor = makeEditor()
    editor.commands.focus()
    typeText(editor, 'a /head')
    // The slash is at position 3, the query ends at 8.
    command('heading2').run(editor, { from: 3, to: 8 })

    // TipTap keeps an empty paragraph after a block that is not one, so there is always a place to type.
    expect(editor.getJSON().content).toEqual([
      { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'a ' }] },
      { type: 'paragraph' },
    ])
  })

  it('turns a paragraph back into text', () => {
    const editor = makeEditor()
    editor.commands.focus()
    command('heading1').run(editor)
    command('text').run(editor)

    expect(editor.getJSON().content![0]).toEqual({ type: 'paragraph' })
  })

  it('makes a table of 3 by 3 with a header row', () => {
    const editor = makeEditor()
    editor.commands.focus()
    command('table').run(editor)
    const table: JSONContent = editor.getJSON().content!.find((node) => node.type === 'table')!
    const cells = (row: JSONContent) => row.content!.map((cell) => cell.type)
    expect(table.content).toHaveLength(3)
    expect(cells(table.content![0]!)).toEqual(['tableHeader', 'tableHeader', 'tableHeader'])
    expect(cells(table.content![1]!)).toEqual(['tableCell', 'tableCell', 'tableCell'])
  })

  it('makes a divider', () => {
    const editor = makeEditor()
    editor.commands.focus()
    command('divider').run(editor)
    expect(editor.getJSON().content!.map((node) => node.type)).toEqual([
      'horizontalRule',
      'paragraph',
    ])
  })

  it.each([
    ['image', 'image/png,image/jpeg,image/gif,image/webp'],
    ['file', ''],
  ])('%s removes what was typed and opens the file picker', (id, accept) => {
    const editor = makeEditor({
      upload: { upload: async () => Promise.reject(), onError: () => {}, onBusyChange: () => {} },
    })
    const pickers: HTMLInputElement[] = []
    vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(function (
      this: HTMLInputElement,
    ) {
      pickers.push(this)
    })
    editor.commands.focus()
    typeText(editor, '/ima')

    command(id).run(editor, { from: 1, to: 5 })

    expect(editor.getJSON().content).toEqual([{ type: 'paragraph' }])
    expect(pickers.map((picker) => picker.accept)).toEqual([accept])
  })

  it('does nothing for an image in an editor that cannot upload', () => {
    const editor = makeEditor()
    const click = vi.spyOn(HTMLInputElement.prototype, 'click')
    editor.commands.focus()

    expect(() => command('image').run(editor)).not.toThrow()
    expect(click).not.toHaveBeenCalled()
  })

  it('offers only the blocks that change a block under "Turn into"', () => {
    expect(blockCommands.filter((block) => block.insertOnly).map((block) => block.id)).toEqual([
      'image',
      'file',
    ])
  })

  it('has an id, a label, a description and search words, in both languages, for each block', () => {
    for (const t of [english, dutch]) {
      for (const block of blockCommands) {
        expect(block.label(t), block.id).not.toMatch(/^editor\./)
        expect(block.description(t), block.id).not.toMatch(/^editor\./)
        expect(block.keywords(t), block.id).not.toMatch(/^editor\./)
      }
    }
    expect(new Set(blockCommands.map((block) => block.id)).size).toBe(blockCommands.length)
  })
})

describe('filterCommands', () => {
  it('lists every block, in the order of the menu, when nothing has been typed', () => {
    expect(ids('')).toEqual(blockCommands.map((block) => block.id))
  })

  it('matches the start of a word in the label', () => {
    expect(ids('hea')).toEqual(['heading1', 'heading2', 'heading3'])
    expect(ids('list')).toEqual(['bulletList', 'numberedList'])
    expect(ids('1')).toEqual(['heading1'])
  })

  it('does not match the middle of a word', () => {
    expect(ids('ading')).toEqual([])
  })

  it('matches search words that are not in the label', () => {
    expect(ids('todo')).toEqual(['checklist'])
    expect(ids('hr')).toEqual(['divider'])
    expect(ids('ul')).toEqual(['bulletList'])
  })

  it('ignores case and accents', () => {
    expect(ids('TEXT')).toEqual(['text'])
    expect(ids('tëxt')).toEqual(['text'])
  })

  it('finds the Dutch words in Dutch, and not the English ones', () => {
    expect(ids('kop', dutch)).toEqual(['heading1', 'heading2', 'heading3'])
    expect(ids('lijst', dutch)).toEqual(['bulletList', 'numberedList'])
    expect(ids('taken', dutch)).toEqual(['checklist'])
    expect(ids('heading', dutch)).toEqual([])
  })

  it('finds images and files in both languages', () => {
    expect(ids('photo')).toEqual(['image'])
    expect(ids('pdf')).toEqual(['file'])
    expect(ids('foto', dutch)).toEqual(['image'])
    expect(ids('bijlage', dutch)).toEqual(['file'])
  })

  it('finds nothing for a word nobody knows', () => {
    expect(ids('zzzz')).toEqual([])
  })
})
