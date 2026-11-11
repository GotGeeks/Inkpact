import { afterEach, describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import { OfflineDocument, remoteOrigin, snapshotDelay } from './offlineDocument'
import type { CachedPage, CachedSnapshot, OfflineRepository, PageKey } from './types'

// Counts the whole-document encodes, which are what makes a large page slow to save.
vi.mock('yjs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('yjs')>()
  return { ...actual, encodeStateAsUpdate: vi.fn(actual.encodeStateAsUpdate) }
})
const encodes = vi.mocked(Y.encodeStateAsUpdate)

const page: CachedPage = {
  userId: 'user-a',
  workspaceId: 'workspace-a',
  pageId: 'page-a',
  title: 'Page',
  inTrash: false,
  ownTrash: false,
  canEdit: true,
  ready: true,
  lastOpened: 1,
}

/** Keeps every snapshot written; `hold` makes the next write wait until it is released. */
class Repository implements OfflineRepository {
  readonly saved: Uint8Array[] = []
  private held: Promise<void> | null = null
  private releaseHeld: (() => void) | null = null

  hold() {
    this.held = new Promise((resolve) => (this.releaseHeld = resolve))
  }

  release() {
    this.releaseHeld?.()
    this.held = null
  }

  async findPage() {
    return null
  }
  async readSnapshots(key: PageKey): Promise<CachedSnapshot[]> {
    const last = this.saved.at(-1)
    return last ? [{ ...key, writerId: 'w', update: last, unconfirmed: 0 }] : []
  }
  async saveSnapshot(_metadata: CachedPage, _writer: string, update: Uint8Array) {
    const waiting = this.held
    if (waiting) await waiting
    this.saved.push(update)
  }
  async saveRecovery() {}
  async listRecoveries() {
    return []
  }
  async deleteRecovery() {}
  async cleanup() {
    return 0
  }
  async forgetConfirmed() {
    return 0
  }
}

async function opened(repository: Repository, statuses: boolean[] = []) {
  const seed = new Y.Doc()
  seed.getText('text').insert(0, 'start')
  repository.saved.push(Y.encodeStateAsUpdate(seed))
  seed.destroy()
  const session = await OfflineDocument.open(repository, page, false, (state) =>
    statuses.push(state.pending),
  )
  encodes.mockClear()
  repository.saved.length = 0
  return session
}

afterEach(() => {
  vi.useRealTimers()
})

describe('saving a page on this device', () => {
  it('encodes the page once for a Sync that applies many stored updates one by one', async () => {
    const repository = new Repository()
    const session = await opened(repository)
    const remote = new Y.Doc()
    Y.applyUpdate(remote, Y.encodeStateAsUpdate(session.doc))
    const updates: Uint8Array[] = []
    remote.on('update', (update: Uint8Array) => updates.push(update))
    for (let i = 0; i < 200; i++) remote.getText('text').insert(0, `${i} `)
    encodes.mockClear()

    for (const update of updates) Y.applyUpdate(session.doc, update, remoteOrigin)
    await vi.waitFor(() => expect(repository.saved).toHaveLength(1))

    expect(encodes).toHaveBeenCalledTimes(1)
    const saved = new Y.Doc()
    Y.applyUpdate(saved, repository.saved[0]!)
    expect(saved.getText('text').toString()).toBe(session.doc.getText('text').toString())
    saved.destroy()
    remote.destroy()
    session.detach()
    session.doc.destroy()
  })

  it('folds the keys typed while a snapshot is being written into one more snapshot', async () => {
    const repository = new Repository()
    const session = await opened(repository)
    repository.hold()
    session.doc.getText('text').insert(0, 'a')
    await vi.waitFor(() => expect(encodes).toHaveBeenCalledTimes(1))
    for (const key of 'bcdefghij') session.doc.getText('text').insert(0, key)
    repository.release()

    await vi.waitFor(() => expect(repository.saved).toHaveLength(2))
    expect(encodes).toHaveBeenCalledTimes(2)
    const saved = new Y.Doc()
    Y.applyUpdate(saved, repository.saved[1]!)
    expect(saved.getText('text').toString()).toBe('jihgfedcbastart')
    saved.destroy()
    session.detach()
    session.doc.destroy()
  })

  it('reports the change as pending until its snapshot is written', async () => {
    const repository = new Repository()
    const statuses: boolean[] = []
    const session = await opened(repository, statuses)
    statuses.length = 0
    session.doc.getText('text').insert(0, 'x')
    expect(statuses).toEqual([true])
    await vi.waitFor(() => expect(statuses.at(-1)).toBe(false))
    session.detach()
    session.doc.destroy()
  })

  it('writes the state a document had when it was detached, after the sync client destroyed it', async () => {
    const repository = new Repository()
    const session = await opened(repository)
    repository.hold()
    session.doc.getText('text').insert(0, 'first ')
    await vi.waitFor(() => expect(encodes).toHaveBeenCalled())
    session.doc.getText('text').insert(0, 'last ')
    session.detach()
    session.doc.destroy()
    repository.release()

    await vi.waitFor(() => expect(repository.saved).toHaveLength(2))
    const saved = new Y.Doc()
    Y.applyUpdate(saved, repository.saved[1]!)
    expect(saved.getText('text').toString()).toBe('last first start')
    saved.destroy()
  })

  it('pauses between snapshots of a large page, and writes at once when told the tab is hidden', async () => {
    const repository = new Repository()
    const session = await opened(repository)
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'performance'] })
    // Every encode takes 50 ms, as a page of several MiB can.
    const actual = encodes.getMockImplementation()!
    encodes.mockImplementation((...args) => {
      vi.advanceTimersByTime(50)
      return actual(...args)
    })
    const settle = async () => {
      for (let i = 0; i < 20; i++) await Promise.resolve()
    }

    session.doc.getText('text').insert(0, 'a')
    await settle()
    expect(repository.saved).toHaveLength(1)
    // The next key, after that snapshot was written, waits for the pause.
    session.doc.getText('text').insert(0, 'b')
    await settle()
    expect(repository.saved).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(snapshotDelay(50) - 1)
    expect(repository.saved).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(1)
    await settle()
    expect(repository.saved).toHaveLength(2)

    session.doc.getText('text').insert(0, 'c')
    await settle()
    expect(repository.saved).toHaveLength(2)
    session.flushNow()
    await settle()
    expect(repository.saved).toHaveLength(3)
    const saved = new Y.Doc()
    Y.applyUpdate(saved, repository.saved[2]!)
    expect(saved.getText('text').toString()).toBe('cbastart')
    saved.destroy()
    encodes.mockImplementation(actual)
    session.detach()
    session.doc.destroy()
  })

  it('does not write the page again when the tree changes but its metadata does not', async () => {
    const repository = new Repository()
    const session = await opened(repository)
    session.refreshMetadata({ ...page })
    await Promise.resolve()
    await Promise.resolve()
    expect(repository.saved).toHaveLength(0)
    expect(encodes).not.toHaveBeenCalled()

    session.refreshMetadata({ ...page, title: 'Renamed' })
    await vi.waitFor(() => expect(repository.saved).toHaveLength(1))
    expect(session.metadata.title).toBe('Renamed')
    session.detach()
    session.doc.destroy()
  })

  it('stops caching a confirmed page when its account signs out, so removing it sticks', async () => {
    const repository = new Repository()
    const session = await opened(repository)
    expect(session.stopCaching()).toBe(true)
    session.markReady()
    session.refreshMetadata({ ...page, title: 'Renamed' })
    await Promise.resolve()
    await Promise.resolve()
    expect(repository.saved).toHaveLength(0)
    session.doc.destroy()
  })

  it('keeps saving a page with edits the server has not confirmed, even when its account signs out', async () => {
    const repository = new Repository()
    const session = await opened(repository)
    session.doc.getText('text').insert(0, 'unsent ')
    expect(session.stopCaching()).toBe(false)
    await vi.waitFor(() => expect(repository.saved).toHaveLength(1))
    session.detach()
    session.doc.destroy()
  })

  it('waits nothing after a quick encode, and at most two seconds after a slow one', () => {
    expect(snapshotDelay(0.5)).toBe(0)
    expect(snapshotDelay(3.9)).toBe(0)
    expect(snapshotDelay(4)).toBe(32)
    expect(snapshotDelay(50)).toBe(400)
    expect(snapshotDelay(1000)).toBe(2000)
  })
})
