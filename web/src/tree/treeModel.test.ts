import { describe, expect, it } from 'vitest'
import {
  ancestorIds,
  descendantIds,
  dropPosition,
  insertIndex,
  isNoMove,
  keyboardMove,
  moveTargets,
  visibleRows,
  zoneAt,
} from './treeModel'
import type { PageNode } from './types'

/** Builds pages from an outline: two spaces of indent per level, in tree order. Ids are the titles. */
function outline(lines: string[]): PageNode[] {
  const stack: string[] = []
  return lines.map((line, i) => {
    const level = (line.length - line.trimStart().length) / 2
    const title = line.trim()
    stack.length = level
    const parentId = level === 0 ? null : stack[level - 1]!
    stack[level] = title
    return {
      id: title,
      parentId,
      title,
      sortKey: `k${i}`,
      version: 1,
      updatedAt: '2026-10-26T10:00:00Z',
    }
  })
}

const sample = outline(['A', '  A1', '    A1a', '  A2', 'B', 'C', '  C1'])

describe('visibleRows', () => {
  it('shows only the top level when nothing is expanded', () => {
    expect(visibleRows(sample, new Set()).map((r) => r.page.id)).toEqual(['A', 'B', 'C'])
  })

  it('shows the children of what is expanded, parents first, at the right depth', () => {
    const rows = visibleRows(sample, new Set(['A', 'A1']))

    expect(rows.map((r) => [r.page.id, r.level])).toEqual([
      ['A', 1],
      ['A1', 2],
      ['A1a', 3],
      ['A2', 2],
      ['B', 1],
      ['C', 1],
    ])
  })

  it('does not show a branch whose parent is collapsed, even if it was expanded itself', () => {
    expect(visibleRows(sample, new Set(['A1'])).map((r) => r.page.id)).toEqual(['A', 'B', 'C'])
  })

  it('says whether a row has children and whether it is open', () => {
    const rows = visibleRows(sample, new Set(['A']))

    expect(rows.find((r) => r.page.id === 'A')).toMatchObject({ hasChildren: true, expanded: true })
    expect(rows.find((r) => r.page.id === 'A1')).toMatchObject({
      hasChildren: true,
      expanded: false,
    })
    expect(rows.find((r) => r.page.id === 'A2')).toMatchObject({
      hasChildren: false,
      expanded: false,
    })
  })

  it('does not call a page without children expanded, whatever the set says', () => {
    expect(visibleRows(sample, new Set(['B'])).find((r) => r.page.id === 'B')!.expanded).toBe(false)
  })

  it('counts siblings for the screen reader', () => {
    const rows = visibleRows(sample, new Set(['A']))

    expect(rows.filter((r) => r.level === 1).map((r) => [r.position, r.setSize])).toEqual([
      [1, 3],
      [2, 3],
      [3, 3],
    ])
    expect(rows.filter((r) => r.level === 2).map((r) => [r.position, r.setSize])).toEqual([
      [1, 2],
      [2, 2],
    ])
  })

  it('shows nothing for no pages', () => {
    expect(visibleRows([], new Set())).toEqual([])
  })
})

describe('descendantIds and ancestorIds', () => {
  it('finds everything below a page, at any depth', () => {
    expect([...descendantIds(sample, 'A')].sort()).toEqual(['A1', 'A1a', 'A2'])
    expect([...descendantIds(sample, 'A1a')]).toEqual([])
  })

  it('finds the pages above, nearest first', () => {
    expect(ancestorIds(sample, 'A1a')).toEqual(['A1', 'A'])
    expect(ancestorIds(sample, 'A')).toEqual([])
    expect(ancestorIds(sample, 'unknown')).toEqual([])
  })

  it('does not loop on a broken tree', () => {
    const broken: PageNode[] = [
      { id: 'x', parentId: 'y', title: 'x', sortKey: 'a', version: 1, updatedAt: '' },
      { id: 'y', parentId: 'x', title: 'y', sortKey: 'a', version: 1, updatedAt: '' },
    ]

    expect(ancestorIds(broken, 'x').length).toBeLessThanOrEqual(2)
  })
})

describe('insertIndex', () => {
  const siblings = ['a', 'b', 'c']

  it.each([
    [{}, 3],
    [{ afterId: 'a' }, 1],
    [{ afterId: 'c' }, 3],
    [{ beforeId: 'a' }, 0],
    [{ beforeId: 'c' }, 2],
    [{ afterId: 'a', beforeId: 'b' }, 1],
    [{ afterId: 'b', beforeId: 'c' }, 2],
  ])('puts %j at %i', (position, expected) => {
    expect(insertIndex(siblings, position)).toBe(expected)
  })

  it.each([
    [{ afterId: 'zzz' }],
    [{ beforeId: 'zzz' }],
    [{ afterId: 'a', beforeId: 'c' }], // not next to each other
    [{ afterId: 'b', beforeId: 'a' }], // the wrong way round
  ])('refuses %j', (position) => {
    expect(insertIndex(siblings, position)).toBe(-1)
  })
})

describe('isNoMove', () => {
  it('is true for where a page already is', () => {
    expect(isNoMove(sample, 'B', { parentId: null, afterId: 'A' })).toBe(true)
    expect(isNoMove(sample, 'B', { parentId: null, beforeId: 'C' })).toBe(true)
    expect(isNoMove(sample, 'C', { parentId: null })).toBe(true) // last at the top level
    expect(isNoMove(sample, 'A2', { parentId: 'A' })).toBe(true) // last child of A
  })

  it('is false for any other place', () => {
    expect(isNoMove(sample, 'B', { parentId: null, afterId: 'C' })).toBe(false)
    expect(isNoMove(sample, 'A', { parentId: null })).toBe(false)
    expect(isNoMove(sample, 'B', { parentId: 'A' })).toBe(false) // another parent
    expect(isNoMove(sample, 'unknown', { parentId: null })).toBe(false)
  })
})

describe('zoneAt', () => {
  it.each([
    [0, 'before'],
    [4, 'before'],
    [12, 'inside'],
    [20, 'inside'],
    [31, 'after'],
    [40, 'after'],
  ])('at %d px of a 40 px row is %s', (offset, zone) => {
    expect(zoneAt(offset, 40)).toBe(zone)
  })

  it('is the middle of a row with no height', () => {
    expect(zoneAt(0, 0)).toBe('inside')
  })
})

describe('dropPosition', () => {
  it('names the row below as the neighbour for the top part, and the row above for the bottom part', () => {
    expect(dropPosition(sample, 'C', 'B', 'before')).toEqual({ parentId: null, beforeId: 'B' })
    expect(dropPosition(sample, 'C', 'A', 'after')).toEqual({ parentId: null, afterId: 'A' })
  })

  it('makes the page the last child for the middle part', () => {
    expect(dropPosition(sample, 'C', 'B', 'inside')).toEqual({ parentId: 'B' })
  })

  it('works at a deeper level', () => {
    expect(dropPosition(sample, 'B', 'A1', 'after')).toEqual({ parentId: 'A', afterId: 'A1' })
    expect(dropPosition(sample, 'A2', 'A1a', 'before')).toEqual({ parentId: 'A1', beforeId: 'A1a' })
  })

  it.each(['before', 'inside', 'after'] as const)('refuses a drop on itself (%s)', (zone) => {
    expect(dropPosition(sample, 'A', 'A', zone)).toBeNull()
  })

  it.each(['before', 'inside', 'after'] as const)(
    'refuses a drop on something below it (%s)',
    (zone) => {
      expect(dropPosition(sample, 'A', 'A1', zone)).toBeNull()
      expect(dropPosition(sample, 'A', 'A1a', zone)).toBeNull()
    },
  )

  it('refuses a drop that changes nothing', () => {
    expect(dropPosition(sample, 'B', 'A', 'after')).toBeNull()
    expect(dropPosition(sample, 'B', 'C', 'before')).toBeNull()
    expect(dropPosition(sample, 'A2', 'A', 'inside')).toBeNull()
  })

  it('refuses a drop on a page that does not exist', () => {
    expect(dropPosition(sample, 'A', 'ghost', 'inside')).toBeNull()
  })

  it('allows a drop next to a descendant of another branch, and next to the parent of the dragged page', () => {
    expect(dropPosition(sample, 'A1a', 'A', 'after')).toEqual({ parentId: null, afterId: 'A' })
    expect(dropPosition(sample, 'A1a', 'A2', 'before')).toEqual({ parentId: 'A', beforeId: 'A2' })
  })
})

describe('keyboardMove', () => {
  it('moves up before the previous sibling, and not past the first', () => {
    expect(keyboardMove(sample, 'C', 'up')).toEqual({ parentId: null, beforeId: 'B' })
    expect(keyboardMove(sample, 'A2', 'up')).toEqual({ parentId: 'A', beforeId: 'A1' })
    expect(keyboardMove(sample, 'A', 'up')).toBeNull()
    expect(keyboardMove(sample, 'A1', 'up')).toBeNull()
  })

  it('moves down after the next sibling, and not past the last', () => {
    expect(keyboardMove(sample, 'A', 'down')).toEqual({ parentId: null, afterId: 'B' })
    expect(keyboardMove(sample, 'A1', 'down')).toEqual({ parentId: 'A', afterId: 'A2' })
    expect(keyboardMove(sample, 'C', 'down')).toBeNull()
    expect(keyboardMove(sample, 'A2', 'down')).toBeNull()
  })

  it('indents into the sibling above as its last child, and not without one', () => {
    expect(keyboardMove(sample, 'B', 'indent')).toEqual({ parentId: 'A' })
    expect(keyboardMove(sample, 'A2', 'indent')).toEqual({ parentId: 'A1' })
    expect(keyboardMove(sample, 'A', 'indent')).toBeNull()
    expect(keyboardMove(sample, 'A1', 'indent')).toBeNull()
  })

  it('outdents to just after its parent, and not out of the top level', () => {
    expect(keyboardMove(sample, 'A1', 'outdent')).toEqual({ parentId: null, afterId: 'A' })
    expect(keyboardMove(sample, 'A1a', 'outdent')).toEqual({ parentId: 'A', afterId: 'A1' })
    expect(keyboardMove(sample, 'A', 'outdent')).toBeNull()
  })

  it('does nothing for a page that does not exist', () => {
    for (const direction of ['up', 'down', 'indent', 'outdent'] as const) {
      expect(keyboardMove(sample, 'ghost', direction)).toBeNull()
    }
  })

  it('never offers a move that the drop rules would call no move', () => {
    for (const page of sample) {
      for (const direction of ['up', 'down', 'indent', 'outdent'] as const) {
        const position = keyboardMove(sample, page.id, direction)
        if (position)
          expect(isNoMove(sample, page.id, position), `${page.id} ${direction}`).toBe(false)
      }
    }
  })

  it('never offers a position the server would refuse', () => {
    for (const page of sample) {
      for (const direction of ['up', 'down', 'indent', 'outdent'] as const) {
        const position = keyboardMove(sample, page.id, direction)
        if (!position) continue
        const siblings = sample
          .filter((p) => p.parentId === position.parentId && p.id !== page.id)
          .map((p) => p.id)
        expect(insertIndex(siblings, position), `${page.id} ${direction}`).toBeGreaterThanOrEqual(0)
        expect(descendantIds(sample, page.id).has(position.parentId ?? '')).toBe(false)
        expect(position.parentId).not.toBe(page.id)
      }
    }
  })
})

describe('moveTargets', () => {
  it('lists every page except the one moving and what is below it, expanded and indented', () => {
    const targets = moveTargets(sample, 'A1')

    expect(targets.map((r) => [r.page.id, r.level])).toEqual([
      ['A', 1],
      ['A2', 2],
      ['B', 1],
      ['C', 1],
      ['C1', 2],
    ])
  })

  it('leaves out a whole branch for its root', () => {
    expect(moveTargets(sample, 'A').map((r) => r.page.id)).toEqual(['B', 'C', 'C1'])
  })
})
