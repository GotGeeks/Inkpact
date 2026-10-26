import { Extension, type Editor } from '@tiptap/core'
import type { Node as ProseMirrorNode, ResolvedPos } from '@tiptap/pm/model'
import { Selection, TextSelection, type Transaction } from '@tiptap/pm/state'

/** A block of the page: a top-level node, or an item of a list. */
export interface BlockRange {
  node: ProseMirrorNode
  from: number
  to: number
  /** The node that holds it: the document, or a list. */
  parent: ProseMirrorNode
  index: number
}

const listItems = new Set(['listItem', 'taskItem'])

/** The block around a position: the list item when the position is in a list, else the top-level block. */
export function blockAround($pos: ResolvedPos): BlockRange | null {
  for (let depth = $pos.depth; depth >= 1; depth--) {
    const node = $pos.node(depth)
    if (listItems.has(node.type.name)) return range($pos, depth)
  }
  return $pos.depth >= 1 ? range($pos, 1) : null
}

/** The top-level block that starts at `pos`, as the drag handle reports it. */
export function topLevelBlockAt(doc: ProseMirrorNode, pos: number): BlockRange | null {
  if (pos < 0 || pos >= doc.content.size) return null
  return range(doc.resolve(pos + 1), 1)
}

function range($pos: ResolvedPos, depth: number): BlockRange {
  const from = $pos.before(depth)
  const node = $pos.node(depth)
  return {
    node,
    from,
    to: from + node.nodeSize,
    parent: $pos.node(depth - 1),
    index: $pos.index(depth - 1),
  }
}

/** Puts the caret in the block, so the commands that work on the selection work on it. */
function selectInside(tr: Transaction, block: BlockRange): Transaction {
  const inside = Selection.near(tr.doc.resolve(Math.min(block.from + 1, tr.doc.content.size)), 1)
  return tr.setSelection(inside)
}

export function selectBlock(editor: Editor, block: BlockRange) {
  editor.view.dispatch(selectInside(editor.state.tr, block))
}

/**
 * Swaps a block with its neighbour in one transaction (one undo step), and keeps the caret where it was inside it.
 * A list item stays in its list. Returns false at the ends.
 */
export function moveBlock(editor: Editor, direction: -1 | 1, at?: BlockRange): boolean {
  const { state } = editor
  const block = at ?? blockAround(state.selection.$from)
  if (!block) return false
  const neighbourIndex = block.index + direction
  if (neighbourIndex < 0 || neighbourIndex >= block.parent.childCount) return false

  const neighbour = block.parent.child(neighbourIndex)
  const tr = state.tr
  const caretOffset = state.selection.from - block.from
  const inside = state.selection.from >= block.from && state.selection.to <= block.to
  let newFrom: number
  if (direction === -1) {
    const start = block.from - neighbour.nodeSize
    tr.replaceWith(start, block.to, [block.node, neighbour])
    newFrom = start
  } else {
    const end = block.to + neighbour.nodeSize
    tr.replaceWith(block.from, end, [neighbour, block.node])
    newFrom = block.from + neighbour.nodeSize
  }
  tr.setSelection(
    inside
      ? TextSelection.near(tr.doc.resolve(Math.min(newFrom + caretOffset, tr.doc.content.size)))
      : Selection.near(tr.doc.resolve(newFrom + 1)),
  )
  editor.view.dispatch(tr.scrollIntoView())
  return true
}

/** A copy right after the block. */
export function duplicateBlock(editor: Editor, block: BlockRange) {
  const tr = editor.state.tr.insert(block.to, block.node.copy(block.node.content))
  editor.view.dispatch(
    tr.setSelection(Selection.near(tr.doc.resolve(block.to + 1))).scrollIntoView(),
  )
}

/** Removes the block; the last block of the page, or the last item of a list, is emptied instead. */
export function deleteBlock(editor: Editor, block: BlockRange) {
  const { schema } = editor
  const tr = editor.state.tr
  if (block.parent.childCount === 1) {
    if (block.parent.type.name === 'doc') {
      tr.replaceWith(block.from, block.to, schema.nodes.paragraph!.create())
    } else {
      // The only item of a list: the list goes with it.
      const list = tr.doc.resolve(block.from).depth
      const listStart = tr.doc.resolve(block.from).before(list)
      const listNode = tr.doc.resolve(block.from).node(list)
      if (list === 1 && tr.doc.childCount === 1) {
        tr.replaceWith(listStart, listStart + listNode.nodeSize, schema.nodes.paragraph!.create())
      } else {
        tr.delete(listStart, listStart + listNode.nodeSize)
      }
    }
  } else {
    tr.delete(block.from, block.to)
  }
  editor.view.dispatch(
    tr
      .setSelection(Selection.near(tr.doc.resolve(Math.min(block.from, tr.doc.content.size))))
      .scrollIntoView(),
  )
}

export interface MoveBlockOptions {
  /** Called after a block has moved, so it can be announced. */
  onMoved: (direction: -1 | 1) => void
}

/** `Alt+ArrowUp` and `Alt+ArrowDown` move the block that holds the caret: the way to do it without a mouse. */
export function createMoveBlockExtension({ onMoved }: MoveBlockOptions) {
  return Extension.create({
    name: 'moveBlock',
    addKeyboardShortcuts() {
      const move = (direction: -1 | 1) => () => {
        if (!this.editor.isEditable) return false
        const moved = moveBlock(this.editor, direction)
        if (moved) onMoved(direction)
        // Alt+Arrow means nothing else in a page, so it is ours even at the ends.
        return true
      }
      return { 'Alt-ArrowUp': move(-1), 'Alt-ArrowDown': move(1) }
    },
  })
}
