import type { PageDetail, PageNode } from '../tree/types'

/**
 * Whether the page on the screen is still what the server has, judged by the tree this tab holds: the page is in the
 * live tree (not in the trash, nor below one) with the version that was fetched. Every change to a page's own row (a
 * rename, a move, the trash) raises its version, so an unchanged version means a change somewhere else in the tree,
 * which needs no new fetch of this page. A page that is in the trash, or that the tree does not have (another
 * workspace, below a page in the trash), is never taken as current.
 */
export function shownPageIsCurrent(detail: PageDetail | null, node: PageNode | undefined): boolean {
  return (
    detail !== null &&
    node !== undefined &&
    !detail.inTrash &&
    node.id === detail.page.id &&
    node.version === detail.page.version
  )
}
