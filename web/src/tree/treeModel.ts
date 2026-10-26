import type { PageNode, Position } from './types'

/** One line of the sidebar: a page, how deep it is, and what can be done to its branch. */
export interface Row {
  page: PageNode
  /** 1 for the top level, as aria-level counts. */
  level: number
  hasChildren: boolean
  expanded: boolean
  /** Place among its siblings, 1-based, and how many there are, for aria-posinset and aria-setsize. */
  position: number
  setSize: number
}

/** Pages grouped by parent. The server sends the tree in order, so each group is in sibling order. */
export function groupByParent(pages: readonly PageNode[]): Map<string | null, PageNode[]> {
  const groups = new Map<string | null, PageNode[]>()
  for (const page of pages) {
    const siblings = groups.get(page.parentId)
    if (siblings) siblings.push(page)
    else groups.set(page.parentId, [page])
  }
  return groups
}

/** The lines the sidebar shows: every page whose parents are all expanded, parents before children. */
export function visibleRows(pages: readonly PageNode[], expanded: ReadonlySet<string>): Row[] {
  const groups = groupByParent(pages)
  const rows: Row[] = []

  const walk = (parentId: string | null, level: number) => {
    const siblings = groups.get(parentId) ?? []
    siblings.forEach((page, index) => {
      const hasChildren = (groups.get(page.id)?.length ?? 0) > 0
      const isExpanded = hasChildren && expanded.has(page.id)
      rows.push({
        page,
        level,
        hasChildren,
        expanded: isExpanded,
        position: index + 1,
        setSize: siblings.length,
      })
      if (isExpanded) walk(page.id, level + 1)
    })
  }

  walk(null, 1)
  return rows
}

/** Every page below this one, at any depth. */
export function descendantIds(pages: readonly PageNode[], id: string): Set<string> {
  const groups = groupByParent(pages)
  const found = new Set<string>()
  const stack = [id]
  while (stack.length > 0) {
    for (const child of groups.get(stack.pop()!) ?? []) {
      found.add(child.id)
      stack.push(child.id)
    }
  }
  return found
}

/** The pages above this one, nearest first. */
export function ancestorIds(pages: readonly PageNode[], id: string): string[] {
  const byId = new Map(pages.map((page) => [page.id, page]))
  const ancestors: string[] = []
  let parent = byId.get(id)?.parentId ?? null
  while (parent !== null && !ancestors.includes(parent)) {
    ancestors.push(parent)
    parent = byId.get(parent)?.parentId ?? null
  }
  return ancestors
}

/**
 * Where in the siblings (without the moved page) a position puts a page, or -1 when it names a neighbour that is not
 * one of them. The same rules as the server's, so a drop the client allows is one the server accepts.
 */
export function insertIndex(
  siblingIds: readonly string[],
  position: Pick<Position, 'afterId' | 'beforeId'>,
): number {
  const { afterId, beforeId } = position
  if (afterId) {
    const at = siblingIds.indexOf(afterId)
    if (at < 0) return -1
    if (beforeId && siblingIds[at + 1] !== beforeId) return -1
    return at + 1
  }
  if (beforeId) return siblingIds.indexOf(beforeId)
  return siblingIds.length
}

/** True when moving the page there would leave everything as it is. */
export function isNoMove(pages: readonly PageNode[], id: string, position: Position): boolean {
  const page = pages.find((p) => p.id === id)
  if (!page || page.parentId !== position.parentId) return false

  const siblings = (groupByParent(pages).get(position.parentId) ?? []).map((p) => p.id)
  const others = siblings.filter((sibling) => sibling !== id)
  return insertIndex(others, position) === siblings.indexOf(id)
}

export type DropZone = 'before' | 'inside' | 'after'

/** Which part of a row the pointer is over: the top quarter, the bottom quarter, or the middle. */
export function zoneAt(offsetY: number, height: number): DropZone {
  const fraction = height > 0 ? offsetY / height : 0.5
  return fraction < 0.25 ? 'before' : fraction > 0.75 ? 'after' : 'inside'
}

/**
 * Where dropping `dragged` on `over` puts it, or null when that is not allowed or changes nothing: onto itself, below
 * itself (a page cannot be its own descendant), or where it already is.
 */
export function dropPosition(
  pages: readonly PageNode[],
  dragged: string,
  over: string,
  zone: DropZone,
): Position | null {
  const target = pages.find((p) => p.id === over)
  if (!target || over === dragged || descendantIds(pages, dragged).has(over)) return null

  const position: Position =
    zone === 'before'
      ? { parentId: target.parentId, beforeId: target.id }
      : zone === 'after'
        ? { parentId: target.parentId, afterId: target.id }
        : { parentId: target.id }
  return isNoMove(pages, dragged, position) ? null : position
}

export type MoveDirection = 'up' | 'down' | 'indent' | 'outdent'

/**
 * The position that a keyboard move leads to, or null when the page cannot go that way: up past the first sibling,
 * down past the last, in under a previous sibling it does not have, out of the top level.
 */
export function keyboardMove(
  pages: readonly PageNode[],
  id: string,
  direction: MoveDirection,
): Position | null {
  const page = pages.find((p) => p.id === id)
  if (!page) return null

  const groups = groupByParent(pages)
  const siblings = groups.get(page.parentId) ?? []
  const index = siblings.findIndex((sibling) => sibling.id === id)

  switch (direction) {
    case 'up':
      return index > 0 ? { parentId: page.parentId, beforeId: siblings[index - 1]!.id } : null
    case 'down':
      return index >= 0 && index < siblings.length - 1
        ? { parentId: page.parentId, afterId: siblings[index + 1]!.id }
        : null
    case 'indent':
      // Becomes the last child of the sibling just above.
      return index > 0 ? { parentId: siblings[index - 1]!.id } : null
    case 'outdent': {
      const parent = pages.find((p) => p.id === page.parentId)
      return parent ? { parentId: parent.parentId, afterId: parent.id } : null
    }
  }
}

/** Pages a person can choose as a new parent in the Move dialog: every page except the one moving and what is below it. */
export function moveTargets(pages: readonly PageNode[], moving: string): Row[] {
  const blocked = descendantIds(pages, moving)
  blocked.add(moving)
  const all = new Set(pages.map((p) => p.id))
  return visibleRows(pages, all).filter((row) => !blocked.has(row.page.id))
}
