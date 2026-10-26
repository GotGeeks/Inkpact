import type { Editor, JSONContent } from '@tiptap/core'
import { Selection } from '@tiptap/pm/state'
import { yUndoPluginKey } from '@tiptap/y-tiptap'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { destroyEditors, makeEditor } from '../testing/editor'
import {
  blockAround,
  deleteBlock,
  duplicateBlock,
  moveBlock,
  topLevelBlockAt,
} from './blockActions'

afterEach(destroyEditors)

type Spec = { t: 'p' | 'h' | 'quote'; text: string } | { t: 'ul'; items: string[] }

function toJson(spec: Spec): JSONContent {
  const text = (value: string): JSONContent[] => [{ type: 'text', text: value }]
  if (spec.t === 'ul') {
    return {
      type: 'bulletList',
      content: spec.items.map((item) => ({
        type: 'listItem',
        content: [{ type: 'paragraph', content: text(item) }],
      })),
    }
  }
  const paragraph: JSONContent = { type: 'paragraph', content: text(spec.text) }
  if (spec.t === 'quote') return { type: 'blockquote', content: [paragraph] }
  if (spec.t === 'h') return { type: 'heading', attrs: { level: 2 }, content: text(spec.text) }
  return paragraph
}

function editorWith(specs: Spec[], options: Parameters<typeof makeEditor>[0] = {}): Editor {
  const editor = makeEditor(options)
  editor.commands.setContent({ type: 'doc', content: specs.map(toJson) })
  return editor
}

/** What the page holds, top-level block by top-level block, as text; empty paragraphs are not counted. */
function blocksOf(editor: Editor): string[] {
  const out: string[] = []
  editor.state.doc.forEach((node) => {
    if (node.type.name === 'bulletList') {
      const items: string[] = []
      node.forEach((item) => items.push(item.textContent))
      out.push(`ul[${items.join(',')}]`)
    } else if (node.type.name === 'paragraph' && node.content.size === 0) {
      return
    } else out.push(`${node.type.name}:${node.textContent}`)
  })
  return out
}

function caretIn(editor: Editor, text: string) {
  let found = -1
  editor.state.doc.descendants((node, pos) => {
    if (found < 0 && node.isText && node.text === text) found = pos
    return found < 0
  })
  editor.view.dispatch(
    editor.state.tr.setSelection(Selection.near(editor.state.doc.resolve(found + 1))),
  )
}

function altArrow(editor: Editor, key: 'ArrowUp' | 'ArrowDown') {
  return editor.view.someProp('handleKeyDown', (handler) =>
    handler(editor.view, new KeyboardEvent('keydown', { key, altKey: true })),
  )
}

const p = (text: string): Spec => ({ t: 'p', text })

describe('moving a block', () => {
  it('swaps a block with the one before and after it, and keeps the caret in it', () => {
    const editor = editorWith([p('a'), p('b'), p('c')])
    caretIn(editor, 'b')
    expect(moveBlock(editor, -1)).toBe(true)
    expect(blocksOf(editor)).toEqual(['paragraph:b', 'paragraph:a', 'paragraph:c'])
    expect(editor.state.selection.$from.parent.textContent).toBe('b')

    expect(moveBlock(editor, 1)).toBe(true)
    expect(moveBlock(editor, 1)).toBe(true)
    expect(blocksOf(editor)).toEqual(['paragraph:a', 'paragraph:c', 'paragraph:b'])
  })

  it('stops at the first block', () => {
    const editor = editorWith([p('a'), p('b')])
    caretIn(editor, 'a')
    expect(moveBlock(editor, -1)).toBe(false)
    expect(blocksOf(editor)).toEqual(['paragraph:a', 'paragraph:b'])
  })

  it('moves a list item inside its list, and never out of it', () => {
    const editor = editorWith([p('x'), { t: 'ul', items: ['one', 'two', 'three'] }])
    caretIn(editor, 'three')
    expect(moveBlock(editor, -1)).toBe(true)
    expect(blocksOf(editor)).toEqual(['paragraph:x', 'ul[one,three,two]'])

    caretIn(editor, 'one')
    expect(moveBlock(editor, -1)).toBe(false)
    expect(blocksOf(editor)).toEqual(['paragraph:x', 'ul[one,three,two]'])
  })

  it('moves a quote as a whole block', () => {
    const editor = editorWith([
      { t: 'h', text: 'title' },
      { t: 'quote', text: 'said' },
      { t: 'ul', items: ['a', 'b'] },
    ])
    caretIn(editor, 'said')
    moveBlock(editor, -1)
    expect(blocksOf(editor)).toEqual(['blockquote:said', 'heading:title', 'ul[a,b]'])
  })

  it('is one undo step', () => {
    const editor = editorWith([p('a'), p('b'), p('c')])
    caretIn(editor, 'c')
    // Yjs joins changes that follow each other within half a second into one undo step; the page was only just made.
    yUndoPluginKey.getState(editor.state)!.undoManager.stopCapturing()
    moveBlock(editor, -1)
    expect(blocksOf(editor)).toEqual(['paragraph:a', 'paragraph:c', 'paragraph:b'])
    editor.commands.undo()
    expect(blocksOf(editor)).toEqual(['paragraph:a', 'paragraph:b', 'paragraph:c'])
  })

  it('answers Alt+Arrow, and says which way', () => {
    const moved = vi.fn()
    const editor = editorWith([p('a'), p('b')], { onBlockMoved: moved })
    caretIn(editor, 'b')
    expect(altArrow(editor, 'ArrowUp')).toBe(true)
    expect(blocksOf(editor)).toEqual(['paragraph:b', 'paragraph:a'])
    expect(moved).toHaveBeenCalledWith(-1)
  })

  it('leaves a read-only page alone', () => {
    const editor = editorWith([p('a'), p('b')])
    caretIn(editor, 'b')
    editor.setEditable(false)
    expect(altArrow(editor, 'ArrowUp')).toBeFalsy()
    expect(blocksOf(editor)).toEqual(['paragraph:a', 'paragraph:b'])
  })
})

describe('duplicating and deleting', () => {
  it('duplicates a list and a quote, right after them', () => {
    const editor = editorWith([
      { t: 'ul', items: ['a', 'b'] },
      { t: 'quote', text: 'q' },
    ])
    duplicateBlock(editor, topLevelBlockAt(editor.state.doc, 0)!)
    expect(blocksOf(editor)).toEqual(['ul[a,b]', 'ul[a,b]', 'blockquote:q'])
  })

  it('deletes a block, and empties the page when it was the last one', () => {
    const editor = editorWith([p('a')])
    deleteBlock(editor, topLevelBlockAt(editor.state.doc, 0)!)
    expect(blocksOf(editor)).toEqual([])
    expect(editor.state.doc.childCount).toBeGreaterThanOrEqual(1)
  })

  it('deletes a table', () => {
    const editor = makeEditor()
    editor.commands.insertTable({ rows: 2, cols: 2 })
    expect(editor.state.doc.firstChild!.type.name).toBe('table')
    deleteBlock(editor, topLevelBlockAt(editor.state.doc, 0)!)
    expect(editor.state.doc.firstChild!.type.name).toBe('paragraph')
  })

  it('removes the list with its last item', () => {
    const editor = editorWith([p('x'), { t: 'ul', items: ['only'] }])
    caretIn(editor, 'only')
    deleteBlock(editor, blockAround(editor.state.selection.$from)!)
    expect(blocksOf(editor)).toEqual(['paragraph:x'])
  })
})

/** A small seeded generator, so a failure can be replayed from its seed. */
function random(seed: number) {
  let state = seed
  return () => {
    state = (state + 0x6d2b79f5) | 0
    let t = Math.imul(state ^ (state >>> 15), 1 | state)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

describe('moving, duplicating and deleting never lose or double content (property)', () => {
  it('matches a model of the page after random sequences', () => {
    for (let seed = 1; seed <= 40; seed++) {
      const next = random(seed)
      const pick = (n: number) => Math.floor(next() * n)
      let counter = 0
      const label = () => `t${counter++}`
      const initial: Spec[] = Array.from({ length: 2 + pick(5) }, (): Spec => {
        if (pick(4) === 0) return { t: 'ul', items: Array.from({ length: 1 + pick(3) }, label) }
        return { t: (['p', 'h', 'quote'] as const)[pick(3)]!, text: label() }
      })
      const editor = editorWith(initial)
      let model = blocksOf(editor)
      const trace: string[] = []

      for (let step = 0; step < 30 && model.length > 0; step++) {
        const doc = editor.state.doc
        // Blocks that hold something; the empty paragraph TipTap keeps at the end is not one of them.
        const offsets: number[] = []
        doc.forEach((node, offset) => {
          if (!(node.type.name === 'paragraph' && node.content.size === 0)) offsets.push(offset)
        })
        const at = pick(offsets.length)
        const block = topLevelBlockAt(doc, offsets[at]!)!
        const op = pick(3)
        if (op === 0) {
          const direction = pick(2) === 0 ? -1 : 1
          trace.push(`move ${at} ${direction}`)
          editor.view.dispatch(
            editor.state.tr.setSelection(Selection.near(doc.resolve(block.from + 1))),
          )
          // A list item moves inside its list; the caret is at the start of the first item, so a move up or down
          // of the top-level block is only checked when the block is not a list.
          if (block.node.type.name !== 'bulletList') {
            const target = at + direction
            const neighbour = doc.maybeChild(block.index + direction)
            const swapsContent =
              neighbour !== null &&
              !(neighbour.type.name === 'paragraph' && neighbour.content.size === 0)
            const moved = moveBlock(editor, direction)
            if (moved && swapsContent) {
              ;[model[at], model[target]] = [model[target]!, model[at]!]
            }
          }
        } else if (op === 1) {
          trace.push(`duplicate ${at}`)
          duplicateBlock(editor, block)
          model = [...model.slice(0, at + 1), model[at]!, ...model.slice(at + 1)]
        } else {
          trace.push(`delete ${at}`)
          deleteBlock(editor, block)
          model = model.filter((_, index) => index !== at)
        }
        expect(blocksOf(editor), `seed ${seed}: ${trace.join(', ')}`).toEqual(model)
      }
    }
  })
})
