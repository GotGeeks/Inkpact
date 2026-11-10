import * as Y from 'yjs'
import { applyFirstParagraph } from '../editor/firstParagraph'
import { newPageId } from '../sync/pageCopy'
import type { CachedPage, CachedRecovery, OfflineRepository } from './types'

/** Shared with the sync client so remote changes do not pin an otherwise clean cache entry. */
export const remoteOrigin = Symbol('remote sync update')

export interface Durability {
  pending: boolean
  error: unknown | null
}

interface PendingRecovery {
  recovery: CachedRecovery
  committed: () => void
}

/**
 * One writer per open document: concurrent tabs never overwrite each other's snapshots.
 * The recovery queue is committed before the snapshot that could contain a remote deletion.
 */
export class OfflineDocument {
  readonly doc: Y.Doc
  readonly metadata: CachedPage
  private readonly writerId = newPageId()
  private readonly repository: OfflineRepository
  private readonly onChange: (state: Durability) => void
  private authoritativeMetadata: boolean
  private needsConfirmation: boolean
  private snapshot: Uint8Array | null = null
  private recoveries: PendingRecovery[] = []
  private writing = false
  private failure: unknown | null = null
  private detached = false

  private constructor(
    doc: Y.Doc,
    metadata: CachedPage,
    repository: OfflineRepository,
    unconfirmed: boolean,
    authoritativeMetadata: boolean,
    onChange: (state: Durability) => void,
  ) {
    this.doc = doc
    this.metadata = metadata
    this.repository = repository
    this.needsConfirmation = unconfirmed
    this.authoritativeMetadata = authoritativeMetadata
    this.onChange = onChange
    doc.on('update', this.onUpdate)
  }

  static async open(
    repository: OfflineRepository,
    metadata: CachedPage,
    authoritativeMetadata: boolean,
    onChange: (state: Durability) => void,
  ): Promise<OfflineDocument> {
    const snapshots = await repository.readSnapshots(metadata)
    if (metadata.ready && snapshots.length === 0)
      throw new Error('The cached page has no persisted document')
    const doc = new Y.Doc()
    try {
      applyFirstParagraph(doc)
      for (const snapshot of snapshots) Y.applyUpdate(doc, snapshot.update)
      const session = new OfflineDocument(
        doc,
        { ...metadata },
        repository,
        snapshots.some((snapshot) => snapshot.unconfirmed === 1),
        authoritativeMetadata,
        onChange,
      )
      // Do not expose the document to the editor or network before this transaction has committed.
      await repository.saveSnapshot(
        session.metadata,
        session.writerId,
        Y.encodeStateAsUpdate(doc),
        session.unconfirmed,
        authoritativeMetadata,
        snapshots,
      )
      session.authoritativeMetadata = false
      onChange({ pending: false, error: null })
      return session
    } catch (error) {
      doc.destroy()
      throw error
    }
  }

  get unconfirmed() {
    return this.needsConfirmation
  }

  /** The first complete server Sync is only available offline after its snapshot commits. */
  markReady() {
    if (this.metadata.ready) return
    this.metadata.ready = true
    this.scheduleSnapshot()
  }

  /** Only a completed Sync or a pong after an update can confirm this writer's state. */
  markConfirmed() {
    if (!this.needsConfirmation) return
    this.needsConfirmation = false
    this.scheduleSnapshot()
  }

  /** Refreshes cached display metadata only from a successfully authorized server response. */
  refreshMetadata(metadata: CachedPage) {
    this.metadata.title = metadata.title
    this.metadata.inTrash = metadata.inTrash
    this.metadata.ownTrash = metadata.ownTrash
    this.metadata.canEdit = metadata.canEdit
    this.authoritativeMetadata = true
    this.scheduleSnapshot()
  }

  protect(recovery: CachedRecovery): Promise<void> {
    const result = new Promise<void>((committed) => this.recoveries.push({ recovery, committed }))
    this.report()
    queueMicrotask(() => void this.flush())
    return result
  }

  retry() {
    if (!this.failure) return
    this.failure = null
    this.report()
    void this.flush()
  }

  detach() {
    if (this.detached) return
    this.detached = true
    this.doc.off('update', this.onUpdate)
  }

  private readonly onUpdate = (_update: Uint8Array, origin: unknown) => {
    if (origin !== remoteOrigin) this.needsConfirmation = true
    this.scheduleSnapshot()
  }

  private scheduleSnapshot() {
    this.snapshot = Y.encodeStateAsUpdate(this.doc)
    this.report()
    // A Sync invokes onEditsLost after applying its updates. Give that callback a chance to queue
    // the pre-deletion recovery before writing the new snapshot.
    queueMicrotask(() => void this.flush())
  }

  private report() {
    this.onChange({
      pending: this.writing || this.snapshot !== null || this.recoveries.length > 0,
      error: this.failure,
    })
  }

  private async flush() {
    if (this.writing || this.failure) return
    this.writing = true
    this.report()
    try {
      while (this.recoveries.length > 0 || this.snapshot !== null) {
        const pending = this.recoveries[0]
        if (pending) {
          await this.repository.saveRecovery(pending.recovery)
          this.recoveries.shift()
          pending.committed()
        } else {
          const snapshot = this.snapshot!
          this.snapshot = null
          const unconfirmed = this.unconfirmed
          const authoritative = this.authoritativeMetadata
          this.authoritativeMetadata = false
          try {
            await this.repository.saveSnapshot(
              { ...this.metadata },
              this.writerId,
              snapshot,
              unconfirmed,
              authoritative,
            )
          } catch (error) {
            this.snapshot ??= snapshot
            this.authoritativeMetadata ||= authoritative
            throw error
          }
        }
      }
    } catch (error) {
      console.error('The offline page could not be saved', error)
      this.failure = error
    } finally {
      this.writing = false
      this.report()
    }
  }
}
