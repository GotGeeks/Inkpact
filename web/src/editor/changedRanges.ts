import type { Transaction } from '@tiptap/pm/state'

/**
 * The ranges of the document, as it is after `transaction`, that the transaction changed: what its steps replaced, and what
 * a step that only changes marks or attributes covers (it moves nothing, so its map is empty and its own range says where it
 * was). Each is mapped through the steps after it. For a plugin that must look only at what changed, not at the whole page.
 */
export function changedRanges(transaction: Transaction): [number, number][] {
  const ranges: [number, number][] = []
  transaction.steps.forEach((step, index) => {
    const after = transaction.mapping.slice(index + 1)
    step.getMap().forEach((_oldStart, _oldEnd, newStart, newEnd) => {
      ranges.push([after.map(newStart, -1), after.map(newEnd, 1)])
    })
    const { from, to } = step as unknown as { from?: unknown; to?: unknown }
    if (typeof from === 'number' && typeof to === 'number') {
      const through = transaction.mapping.slice(index)
      ranges.push([through.map(from, -1), through.map(to, 1)])
    }
  })
  return ranges
}
