/** A local partition selects cached data; it is never evidence of server authorization. */
export interface PageKey {
  userId: string
  workspaceId: string
  pageId: string
}

export interface CachedPage extends PageKey {
  title: string
  inTrash: boolean
  ownTrash: boolean
  canEdit: boolean
  /** The server's full initial Sync has been committed locally. */
  ready: boolean
  lastOpened: number
}

export interface CachedSnapshot extends PageKey {
  writerId: string
  update: Uint8Array
  /** Numeric because IndexedDB index keys cannot be booleans. */
  unconfirmed: 0 | 1
}

export interface CachedRecovery extends PageKey {
  id: string
  before: Uint8Array
}

export interface OfflineRepository {
  findPage(userId: string, pageId: string): Promise<CachedPage | null>
  readSnapshots(key: PageKey): Promise<CachedSnapshot[]>
  saveSnapshot(
    metadata: CachedPage,
    writerId: string,
    update: Uint8Array,
    unconfirmed: boolean,
    authoritativeMetadata: boolean,
    mergedSnapshots?: readonly CachedSnapshot[],
  ): Promise<void>
  saveRecovery(recovery: CachedRecovery): Promise<void>
  listRecoveries(userId: string): Promise<CachedRecovery[]>
  deleteRecovery(id: string): Promise<void>
  cleanup(maxCleanPages: number): Promise<number>
}
