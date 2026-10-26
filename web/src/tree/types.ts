/** A page as the server's tree shows it. */
export interface PageNode {
  id: string
  parentId: string | null
  title: string
  sortKey: string
  version: number
  updatedAt: string
  /** Only for pages in the trash. */
  deletedAt?: string
}

/**
 * Where a page goes among its new siblings: under `parentId` (null is the top level), between the two it names, or
 * last when it names none. The client never sends a sort key; the server makes it from the neighbours.
 */
export interface Position {
  parentId: string | null
  afterId?: string | null
  beforeId?: string | null
}

/** What a change did, as the server answers it. */
export interface PageChange {
  page: PageNode
  /** The page had changed since the version the client sent. The change was applied anyway. */
  conflict: boolean
  /** For a restore: back where it was, or to the top level because its parent is gone. */
  restoredTo?: 'original' | 'root'
}

export interface TreeResponse {
  treeVersion: number
  pages: PageNode[]
}

export interface WorkspaceSummary {
  id: string
  name: string
  role: 'owner' | 'editor' | 'viewer'
}

export interface PageDetail {
  page: PageNode
  inTrash: boolean
}
