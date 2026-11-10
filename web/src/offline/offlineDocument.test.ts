import { describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import { FIELD } from '../editor/firstParagraph'
import { OfflineDocument, remoteOrigin, type Durability } from './offlineDocument'
import type {
  CachedPage,
  CachedRecovery,
  CachedSnapshot,
  OfflineRepository,
  PageKey,
} from './types'

const page = (overrides: Partial<CachedPage> = {}): CachedPage => ({
  userId: 'user-a',
  workspaceId: 'workspace-a',
  pageId: 'page-a',
  title: 'Page',
  inTrash: false,
  ownTrash: false,
  canEdit: true,
  ready: false,
  lastOpened: 1,
  ...overrides,
})

const keyOf = ({ userId, workspaceId, pageId }: PageKey) => `${userId}/${workspaceId}/${pageId}`

class MemoryRepository implements OfflineRepository {
  readonly pages = new Map<string, CachedPage>()
  readonly snapshots = new Map<string, CachedSnapshot>()
  readonly recoveries = new Map<string, CachedRecovery>()
  readonly writes: string[] = []
  saveFailure: Error | null = null
  recoveryFailure: Error | null = null

  async findPage(userId: string, pageId: string) {
    return (
      [...this.pages.values()].find((item) => item.userId === userId && item.pageId === pageId) ??
      null
    )
  }

  async readSnapshots(key: PageKey) {
    return [...this.snapshots.values()].filter((item) => keyOf(item) === keyOf(key))
  }

  async saveSnapshot(
    metadata: CachedPage,
    writerId: string,
    update: Uint8Array,
    unconfirmed: boolean,
    authoritativeMetadata: boolean,
    mergedSnapshots: readonly CachedSnapshot[] = [],
  ) {
    this.writes.push('snapshot')
    if (this.saveFailure) throw this.saveFailure
    const previous = this.pages.get(keyOf(metadata))
    const current =
      previous && !authoritativeMetadata ? { ...previous } : { ...previous, ...metadata }
    this.pages.set(keyOf(metadata), {
      ...current,
      ready: !!previous?.ready || metadata.ready,
    })
    this.snapshots.set(`${keyOf(metadata)}/${writerId}`, {
      userId: metadata.userId,
      workspaceId: metadata.workspaceId,
      pageId: metadata.pageId,
      writerId,
      update: update.slice(),
      unconfirmed: unconfirmed ? 1 : 0,
    })
    for (const merged of mergedSnapshots) {
      const key = `${keyOf(metadata)}/${merged.writerId}`
      const current = this.snapshots.get(key)
      if (
        current &&
        current.unconfirmed === merged.unconfirmed &&
        current.update.length === merged.update.length &&
        current.update.every((byte, index) => byte === merged.update[index])
      )
        this.snapshots.delete(key)
    }
  }

  async saveRecovery(recovery: CachedRecovery) {
    this.writes.push('recovery')
    if (this.recoveryFailure) throw this.recoveryFailure
    this.recoveries.set(recovery.id, recovery)
  }

  async listRecoveries(userId: string) {
    return [...this.recoveries.values()].filter((item) => item.userId === userId)
  }

  async deleteRecovery(id: string) {
    this.recoveries.delete(id)
  }

  async cleanup() {
    return 0
  }
}

describe('offline documents', () => {
  it('refuses a ready page whose cached document is missing', async () => {
    const repository = new MemoryRepository()
    await expect(
      OfflineDocument.open(repository, { ...page(), ready: true }, false, () => {}),
    ).rejects.toThrow('no persisted document')
  })

  it('hydrates and commits before returning a document, without duplicating the first paragraph', async () => {
    const repository = new MemoryRepository()
    const initial = await OfflineDocument.open(repository, page(), true, () => {})
    initial.doc.getText('text').insert(0, 'saved')
    await vi.waitFor(() => expect(repository.snapshots.size).toBe(1))
    await vi.waitFor(async () => {
      const saved = new Y.Doc()
      Y.applyUpdate(saved, (await repository.readSnapshots(page()))[0]!.update)
      expect(saved.getText('text').toString()).toBe('saved')
      saved.destroy()
    })
    initial.detach()
    initial.doc.destroy()

    let release!: () => void
    const blocked = new Promise<void>((resolve) => (release = resolve))
    const save = vi.spyOn(repository, 'saveSnapshot').mockImplementationOnce(async (...args) => {
      await blocked
      return MemoryRepository.prototype.saveSnapshot.apply(repository, args)
    })
    let opened = false
    const promise = OfflineDocument.open(repository, page(), false, () => {}).then((result) => {
      opened = true
      return result
    })
    await vi.waitFor(() => expect(save).toHaveBeenCalled())
    expect(opened).toBe(false)
    release()
    const restored = await promise
    expect(restored.doc.getText('text').toString()).toBe('saved')
    expect(restored.doc.getXmlFragment(FIELD).length).toBe(1)
    restored.detach()
    restored.doc.destroy()
  })

  it('keeps concurrent writers and merges their edits without crossing user or workspace partitions', async () => {
    const repository = new MemoryRepository()
    const first = await OfflineDocument.open(repository, page(), true, () => {})
    const second = await OfflineDocument.open(repository, page(), true, () => {})
    first.doc.getText('text').insert(0, 'first')
    second.doc.getText('text').insert(0, 'second')
    await vi.waitFor(async () => {
      const updates = await repository.readSnapshots(page())
      const combined = new Y.Doc()
      for (const update of updates) Y.applyUpdate(combined, update.update)
      expect(combined.getText('text').toString()).toContain('first')
      expect(combined.getText('text').toString()).toContain('second')
      combined.destroy()
    })
    const otherUser = await OfflineDocument.open(
      repository,
      page({ userId: 'user-b' }),
      true,
      () => {},
    )
    const otherWorkspace = await OfflineDocument.open(
      repository,
      page({ workspaceId: 'workspace-b' }),
      true,
      () => {},
    )
    expect(otherUser.doc.getText('text').toString()).toBe('')
    expect(otherWorkspace.doc.getText('text').toString()).toBe('')
    for (const session of [first, second, otherUser, otherWorkspace]) {
      session.detach()
      session.doc.destroy()
    }
  })

  it('only prunes a merged writer if it did not change while another tab was opening', async () => {
    const repository = new MemoryRepository()
    const first = await OfflineDocument.open(repository, page(), true, () => {})
    first.doc.getText('text').insert(0, 'old')
    await vi.waitFor(async () => {
      const saved = new Y.Doc()
      Y.applyUpdate(saved, (await repository.readSnapshots(page()))[0]!.update)
      expect(saved.getText('text').toString()).toBe('old')
      saved.destroy()
    })

    let release!: () => void
    const blocked = new Promise<void>((resolve) => (release = resolve))
    const save = vi.spyOn(repository, 'saveSnapshot').mockImplementationOnce(async (...args) => {
      await blocked
      return MemoryRepository.prototype.saveSnapshot.apply(repository, args)
    })
    const opening = OfflineDocument.open(repository, page(), false, () => {})
    await vi.waitFor(() => expect(save).toHaveBeenCalled())
    first.doc.getText('text').insert(3, '-new')
    await vi.waitFor(async () => {
      const saved = new Y.Doc()
      Y.applyUpdate(saved, (await repository.readSnapshots(page()))[0]!.update)
      expect(saved.getText('text').toString()).toBe('old-new')
      saved.destroy()
    })
    release()
    const second = await opening
    expect(await repository.readSnapshots(page())).toHaveLength(2)
    const merged = await OfflineDocument.open(repository, page(), false, () => {})
    expect(merged.doc.getText('text').toString()).toBe('old-new')
    expect(await repository.readSnapshots(page())).toHaveLength(1)
    for (const session of [first, second, merged]) {
      session.detach()
      session.doc.destroy()
    }
  })

  it('does not replace newer metadata when an older tab saves its document', async () => {
    const repository = new MemoryRepository()
    const first = await OfflineDocument.open(repository, page(), true, () => {})
    const second = await OfflineDocument.open(
      repository,
      page({ title: 'Renamed', canEdit: false }),
      true,
      () => {},
    )
    first.doc.getText('text').insert(0, 'local')
    await vi.waitFor(() => expect(repository.writes).toHaveLength(3))
    expect(await repository.findPage('user-a', 'page-a')).toMatchObject({
      title: 'Renamed',
      canEdit: false,
    })
    for (const session of [first, second]) {
      session.detach()
      session.doc.destroy()
    }
  })

  it('persists confirmation separately for each writer and merges pending writers conservatively', async () => {
    const repository = new MemoryRepository()
    const first = await OfflineDocument.open(repository, page(), true, () => {})
    const second = await OfflineDocument.open(repository, page(), true, () => {})
    first.doc.getText('text').insert(0, 'first')
    second.doc.getText('text').insert(0, 'second')
    await vi.waitFor(async () => {
      expect(
        (await repository.readSnapshots(page())).map((snapshot) => snapshot.unconfirmed),
      ).toEqual([1, 1])
    })

    first.markConfirmed()
    await vi.waitFor(async () => {
      expect(
        (await repository.readSnapshots(page())).map((snapshot) => snapshot.unconfirmed).sort(),
      ).toEqual([0, 1])
    })
    const merged = await OfflineDocument.open(repository, page(), false, () => {})
    expect(merged.unconfirmed).toBe(true)
    expect(merged.doc.getText('text').toString()).toContain('first')
    expect(merged.doc.getText('text').toString()).toContain('second')
    merged.markConfirmed()
    await vi.waitFor(async () =>
      expect(
        (await repository.readSnapshots(page())).map((snapshot) => snapshot.unconfirmed),
      ).toEqual([0]),
    )
    const reopened = await OfflineDocument.open(repository, page(), false, () => {})
    expect(reopened.unconfirmed).toBe(false)
    for (const session of [first, second, merged, reopened]) {
      session.detach()
      session.doc.destroy()
    }
  })

  it('does not prune a writer whose confirmation changed while another tab opened', async () => {
    const repository = new MemoryRepository()
    const first = await OfflineDocument.open(repository, page(), true, () => {})
    first.doc.getText('text').insert(0, 'saved')
    await vi.waitFor(async () =>
      expect((await repository.readSnapshots(page()))[0]!.unconfirmed).toBe(1),
    )
    let release!: () => void
    const blocked = new Promise<void>((resolve) => (release = resolve))
    const save = vi.spyOn(repository, 'saveSnapshot').mockImplementationOnce(async (...args) => {
      await blocked
      return MemoryRepository.prototype.saveSnapshot.apply(repository, args)
    })
    const opening = OfflineDocument.open(repository, page(), false, () => {})
    await vi.waitFor(() => expect(save).toHaveBeenCalled())
    first.markConfirmed()
    await vi.waitFor(async () =>
      expect((await repository.readSnapshots(page()))[0]!.unconfirmed).toBe(0),
    )
    release()
    const second = await opening
    expect(second.unconfirmed).toBe(true)
    expect(
      (await repository.readSnapshots(page())).map((snapshot) => snapshot.unconfirmed).sort(),
    ).toEqual([0, 1])
    for (const session of [first, second]) {
      session.detach()
      session.doc.destroy()
    }
  })

  it('keeps a later edit unconfirmed when an earlier confirmation is still being saved', async () => {
    const repository = new MemoryRepository()
    const session = await OfflineDocument.open(repository, page(), true, () => {})
    session.doc.getText('text').insert(0, 'first')
    await vi.waitFor(async () =>
      expect((await repository.readSnapshots(page()))[0]!.unconfirmed).toBe(1),
    )
    let release!: () => void
    const blocked = new Promise<void>((resolve) => (release = resolve))
    const save = vi.spyOn(repository, 'saveSnapshot').mockImplementationOnce(async (...args) => {
      await blocked
      return MemoryRepository.prototype.saveSnapshot.apply(repository, args)
    })
    session.markConfirmed()
    await vi.waitFor(() => expect(save).toHaveBeenCalled())
    session.doc.getText('text').insert(5, '-later')
    release()
    await vi.waitFor(async () => {
      const snapshot = (await repository.readSnapshots(page()))[0]!
      const saved = new Y.Doc()
      Y.applyUpdate(saved, snapshot.update)
      expect(saved.getText('text').toString()).toBe('first-later')
      expect(snapshot.unconfirmed).toBe(1)
      saved.destroy()
    })
    expect(session.unconfirmed).toBe(true)
    session.detach()
    session.doc.destroy()
  })

  it('retries a failed confirmation write without treating an older snapshot as confirmed', async () => {
    const repository = new MemoryRepository()
    const statuses: Durability[] = []
    const session = await OfflineDocument.open(repository, page(), true, (status) =>
      statuses.push(status),
    )
    session.doc.getText('text').insert(0, 'saved')
    await vi.waitFor(async () =>
      expect((await repository.readSnapshots(page()))[0]!.unconfirmed).toBe(1),
    )
    const failure = new Error('storage is full')
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {})
    repository.saveFailure = failure
    session.markConfirmed()
    await vi.waitFor(() => expect(statuses.at(-1)?.error).toBe(failure))
    expect((await repository.readSnapshots(page()))[0]!.unconfirmed).toBe(1)
    repository.saveFailure = null
    session.retry()
    await vi.waitFor(async () => {
      expect((await repository.readSnapshots(page()))[0]!.unconfirmed).toBe(0)
      expect(statuses.at(-1)).toEqual({ pending: false, error: null })
    })
    session.detach()
    session.doc.destroy()
    logged.mockRestore()
  })

  it('reports pending writes and a failure until a retry really commits', async () => {
    const repository = new MemoryRepository()
    const statuses: Durability[] = []
    const session = await OfflineDocument.open(repository, page(), true, (status) =>
      statuses.push(status),
    )
    const failure = new DOMException('Full', 'QuotaExceededError')
    repository.saveFailure = failure
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {})
    session.doc.getText('text').insert(0, 'unsaved')
    expect(statuses.at(-1)?.pending).toBe(true)
    await vi.waitFor(() => expect(statuses.at(-1)?.error).toBe(failure))
    expect(statuses.at(-1)?.pending).toBe(true)
    repository.saveFailure = null
    session.retry()
    await vi.waitFor(() => expect(statuses.at(-1)).toEqual({ pending: false, error: null }))
    const restored = await OfflineDocument.open(repository, page(), false, () => {})
    expect(restored.doc.getText('text').toString()).toBe('unsaved')
    session.detach()
    session.doc.destroy()
    restored.detach()
    restored.doc.destroy()
    logged.mockRestore()
  })

  it('persists a lost-edit recovery before a remote deletion and blocks the snapshot if recovery fails', async () => {
    const repository = new MemoryRepository()
    const session = await OfflineDocument.open(repository, page(), true, () => {})
    session.doc.getText('text').insert(0, 'offline')
    await vi.waitFor(() => expect(repository.writes).toHaveLength(2))
    repository.writes.length = 0
    const before = Y.encodeStateAsUpdate(session.doc)
    const remote = new Y.Doc()
    Y.applyUpdate(remote, before)
    remote.getText('text').delete(0, 7)
    const failure = new Error('disk full')
    repository.recoveryFailure = failure
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {})

    Y.applyUpdate(session.doc, Y.encodeStateAsUpdate(remote), remoteOrigin)
    session.protect({ ...page(), id: 'recovery-a', before })
    await vi.waitFor(() => expect(repository.writes).toEqual(['recovery']))
    expect((await repository.readSnapshots(page()))[0]!.update).toEqual(before)
    repository.recoveryFailure = null
    session.retry()
    await vi.waitFor(() => expect(repository.writes).toEqual(['recovery', 'recovery', 'snapshot']))
    expect((await repository.listRecoveries('user-a'))[0]!.before).toEqual(before)
    session.detach()
    session.doc.destroy()
    remote.destroy()
    logged.mockRestore()
  })

  it('surfaces an initial persistence failure instead of returning an editable document', async () => {
    const repository = new MemoryRepository()
    repository.saveFailure = new Error('unavailable')
    await expect(OfflineDocument.open(repository, page(), true, () => {})).rejects.toThrow(
      'unavailable',
    )
  })
})
