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

/** Below this an encode is too quick to notice, and the next snapshot is written as soon as the last one is. */
const QUICK_ENCODE_MS = 4
/** At most this long between two snapshots of a large page while it keeps changing. */
const MAX_SNAPSHOT_DELAY_MS = 2000

/**
 * How long to wait before encoding a page again after an encode that took `encodeMs`. A large page (an imported note
 * of several MiB) is encoded whole for each snapshot: without a pause, typing in it would spend most of the main thread
 * on encoding. The pause keeps that to about a ninth of the time, and is cut short when the tab is hidden.
 */
export function snapshotDelay(encodeMs: number): number {
  return encodeMs < QUICK_ENCODE_MS ? 0 : Math.min(MAX_SNAPSHOT_DELAY_MS, Math.ceil(encodeMs * 8))
}

/**
 * One writer per open document: concurrent tabs never overwrite each other's snapshots.
 * The recovery queue is committed before the snapshot that could contain a remote deletion.
 * A change only marks the document dirty; it is encoded when a snapshot is written, so a burst of updates (a Sync that
 * applies a large page's stored updates one by one, or typing while a write is under way) costs one encode, not one each.
 */
export class OfflineDocument {
  doc: Y.Doc
  readonly metadata: CachedPage
  private readonly writerId = newPageId()
  private readonly repository: OfflineRepository
  private readonly onChange: (state: Durability) => void
  private authoritativeMetadata: boolean
  private needsConfirmation: boolean
  private dirty = false
  /** The state when the document was detached: the sync client destroys the document after that. */
  private detachedState: Uint8Array | null = null
  private recoveries: PendingRecovery[] = []
  private writing = false
  private failure: unknown | null = null
  private detached = false
  private wake: (() => void) | null = null
  private flushQueued = false
  private stopped = false
  /** No encode before this time (`performance.now()`): the pause after encoding a large page. */
  private notBefore = 0

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

  /** Joins a document that was edited in memory while browser storage was unavailable. */
  adopt(doc: Y.Doc, unconfirmed: boolean) {
    if (this.doc === doc) return

    const previous = this.doc
    const persisted = Y.encodeStateAsUpdate(previous)
    previous.off('update', this.onUpdate)
    this.doc = doc
    Y.applyUpdate(doc, persisted)
    this.needsConfirmation ||= unconfirmed
    doc.on('update', this.onUpdate)
    previous.destroy()
    this.scheduleSnapshot()
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
    // The page view refreshes on every tree change; an unchanged page is not written again (with its whole document).
    if (
      this.metadata.title === metadata.title &&
      this.metadata.inTrash === metadata.inTrash &&
      this.metadata.ownTrash === metadata.ownTrash &&
      this.metadata.canEdit === metadata.canEdit
    )
      return
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

  /**
   * The account signs out: a document the server has is not cached again, so removing it from the device sticks. A
   * document with unconfirmed edits or a recovery copy still to write keeps being saved, and false says so.
   */
  stopCaching(): boolean {
    if (this.needsConfirmation || this.recoveries.length > 0) return false
    this.stopped = true
    this.dirty = false
    this.detach()
    this.report()
    return true
  }

  /** Writes a waiting snapshot now, without the pause a large page gets: the tab is being hidden and may be closed. */
  flushNow() {
    this.notBefore = 0
    this.wake?.()
  }

  detach() {
    if (this.detached) return
    this.detached = true
    this.doc.off('update', this.onUpdate)
    // Nothing changes it any more; a snapshot still to be written (or retried) is of this state.
    this.detachedState = Y.encodeStateAsUpdate(this.doc)
    this.notBefore = 0
    this.wake?.()
  }

  private readonly onUpdate = (_update: Uint8Array, origin: unknown) => {
    if (origin !== remoteOrigin) this.needsConfirmation = true
    this.scheduleSnapshot()
  }

  private scheduleSnapshot() {
    if (this.stopped) return
    const wasDirty = this.dirty
    this.dirty = true
    if (!wasDirty) this.report()
    if (this.flushQueued) return
    this.flushQueued = true
    // A Sync invokes onEditsLost after applying its updates. Give that callback a chance to queue
    // the pre-deletion recovery before writing the new snapshot.
    queueMicrotask(() => {
      this.flushQueued = false
      void this.flush()
    })
  }

  private report() {
    this.onChange({
      pending: this.writing || this.dirty || this.recoveries.length > 0,
      error: this.failure,
    })
  }

  private encode(): Uint8Array {
    return this.detachedState ?? Y.encodeStateAsUpdate(this.doc)
  }

  /** Waits `ms` before the next encode, or less when `flushNow` or `detach` wakes it. */
  private pause(ms: number): Promise<void> {
    return new Promise<void>((resolve) => {
      const finish = () => {
        clearTimeout(timer)
        if (this.wake === finish) this.wake = null
        resolve()
      }
      const timer = setTimeout(finish, ms)
      this.wake = finish
    })
  }

  private async flush() {
    if (this.writing || this.failure) return
    this.writing = true
    this.report()
    try {
      while (this.recoveries.length > 0 || (this.dirty && !this.stopped)) {
        const pending = this.recoveries[0]
        const wait = this.notBefore - performance.now()
        if (pending) {
          await this.repository.saveRecovery(pending.recovery)
          this.recoveries.shift()
          pending.committed()
        } else if (wait > 0) {
          // A recovery queued meanwhile is written first, when the loop comes round again.
          await this.pause(wait)
        } else {
          this.dirty = false
          const started = performance.now()
          const snapshot = this.encode()
          const finished = performance.now()
          this.notBefore = finished + snapshotDelay(finished - started)
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
            this.dirty = true
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
