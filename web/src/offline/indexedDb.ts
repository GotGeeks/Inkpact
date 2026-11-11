import type {
  CachedPage,
  CachedRecovery,
  CachedSnapshot,
  OfflineRepository,
  PageKey,
} from './types'

const databaseName = 'inkpact-offline'
const pageKey = ({ userId, workspaceId, pageId }: PageKey) => [userId, workspaceId, pageId]

function completed(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve()
    transaction.onabort = () =>
      reject(transaction.error ?? new Error('IndexedDB transaction aborted'))
    transaction.onerror = () =>
      reject(transaction.error ?? new Error('IndexedDB transaction failed'))
  })
}

function sameBytes(left: Uint8Array, right: Uint8Array): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index])
}

/** A request is not durable until its transaction completes, even after the request itself succeeds. */
export async function openOfflineDatabase(
  factory: IDBFactory = indexedDB,
): Promise<OfflineRepository> {
  const database = await new Promise<IDBDatabase>((resolve, reject) => {
    const request = factory.open(databaseName, 2)
    let settled = false
    request.onupgradeneeded = (event) => {
      const db = request.result
      if (event.oldVersion === 0) {
        const pages = db.createObjectStore('pages', {
          keyPath: ['userId', 'workspaceId', 'pageId'],
        })
        pages.createIndex('byUserPage', ['userId', 'pageId'], { unique: true })
        const snapshots = db.createObjectStore('snapshots', {
          keyPath: ['userId', 'workspaceId', 'pageId', 'writerId'],
        })
        snapshots.createIndex('byPage', ['userId', 'workspaceId', 'pageId'])
        const recoveries = db.createObjectStore('recoveries', { keyPath: 'id' })
        recoveries.createIndex('byUser', 'userId')
      }
      if (event.oldVersion < 2) {
        const transaction = request.transaction!
        const snapshots = transaction.objectStore('snapshots')
        snapshots.createIndex('byPageAndUnconfirmed', [
          'userId',
          'workspaceId',
          'pageId',
          'unconfirmed',
        ])
        transaction
          .objectStore('recoveries')
          .createIndex('byPage', ['userId', 'workspaceId', 'pageId'])
        if (event.oldVersion > 0) {
          const cursor = snapshots.openCursor()
          cursor.onsuccess = () => {
            const entry = cursor.result
            if (!entry) return
            entry.update({ ...entry.value, unconfirmed: 1 })
            entry.continue()
          }
        }
      }
    }
    request.onblocked = () => {
      settled = true
      reject(new Error('IndexedDB upgrade is blocked by another tab'))
    }
    request.onerror = () => {
      settled = true
      reject(request.error ?? new Error('IndexedDB could not be opened'))
    }
    request.onsuccess = () => {
      if (settled) {
        request.result.close()
        return
      }
      settled = true
      request.result.onversionchange = () => request.result.close()
      resolve(request.result)
    }
  })

  return {
    async findPage(userId, pageId) {
      const transaction = database.transaction('pages', 'readonly')
      const request = transaction.objectStore('pages').index('byUserPage').get([userId, pageId])
      await completed(transaction)
      return (request.result as CachedPage | undefined) ?? null
    },

    async readSnapshots(key) {
      const transaction = database.transaction('snapshots', 'readonly')
      const request = transaction
        .objectStore('snapshots')
        .index('byPage')
        .getAll(IDBKeyRange.only(pageKey(key)))
      await completed(transaction)
      return request.result as CachedSnapshot[]
    },

    async saveSnapshot(
      metadata,
      writerId,
      update,
      unconfirmed,
      authoritativeMetadata,
      mergedSnapshots = [],
    ) {
      const transaction = database.transaction(['pages', 'snapshots'], 'readwrite')
      const pages = transaction.objectStore('pages')
      const snapshots = transaction.objectStore('snapshots')
      const get = pages.get(pageKey(metadata))
      get.onsuccess = () => {
        const previous = get.result as CachedPage | undefined
        const current =
          previous && !authoritativeMetadata
            ? { ...previous, ready: previous.ready || metadata.ready }
            : { ...previous, ...metadata, ready: !!previous?.ready || metadata.ready }
        pages.put({ ...current, lastOpened: Date.now() } satisfies CachedPage)
      }
      snapshots.put({
        ...pageKeyFields(metadata),
        writerId,
        update,
        unconfirmed: unconfirmed ? 1 : 0,
      } satisfies CachedSnapshot)
      // Another tab may have written since hydration; only remove the exact versions merged above.
      for (const merged of mergedSnapshots) {
        const key = [...pageKey(metadata), merged.writerId]
        const read = snapshots.get(key)
        read.onsuccess = () => {
          const current = read.result as CachedSnapshot | undefined
          if (
            current &&
            current.unconfirmed === merged.unconfirmed &&
            sameBytes(current.update, merged.update)
          )
            snapshots.delete(key)
        }
      }
      await completed(transaction)
    },

    async saveRecovery(recovery) {
      const transaction = database.transaction('recoveries', 'readwrite')
      transaction.objectStore('recoveries').put(recovery)
      await completed(transaction)
    },

    async listRecoveries(userId) {
      const transaction = database.transaction('recoveries', 'readonly')
      const request = transaction.objectStore('recoveries').index('byUser').getAll(userId)
      await completed(transaction)
      return request.result as CachedRecovery[]
    },

    async deleteRecovery(id) {
      const transaction = database.transaction('recoveries', 'readwrite')
      transaction.objectStore('recoveries').delete(id)
      await completed(transaction)
    },

    async cleanup(maxCleanPages) {
      const transaction = database.transaction(['pages', 'snapshots', 'recoveries'], 'readwrite')
      const stores = storesOf(transaction)
      const request = stores.pages.getAll()
      let removed = 0
      request.onsuccess = () => {
        const candidates = (request.result as CachedPage[]).filter((page) => page.ready)
        findClean(stores, candidates, (clean) => {
          clean.sort((a, b) => b.lastOpened - a.lastOpened)
          for (const old of clean.slice(maxCleanPages)) {
            removePage(stores, old)
            removed++
          }
        })
      }
      await completed(transaction)
      return removed
    },

    async forgetConfirmed(userId) {
      const transaction = database.transaction(['pages', 'snapshots', 'recoveries'], 'readwrite')
      const stores = storesOf(transaction)
      // Every page key of this account: arrays sort after strings, so [userId, []] is above all of them.
      const request = stores.pages.getAll(IDBKeyRange.bound([userId], [userId, []]))
      let removed = 0
      request.onsuccess = () => {
        findClean(stores, request.result as CachedPage[], (clean) => {
          for (const page of clean) {
            removePage(stores, page)
            removed++
          }
        })
      }
      await completed(transaction)
      return removed
    },
  }
}

interface CleanupStores {
  pages: IDBObjectStore
  snapshots: IDBObjectStore
  byPage: IDBIndex
  unconfirmed: IDBIndex
  recoveries: IDBIndex
}

function storesOf(transaction: IDBTransaction): CleanupStores {
  const snapshots = transaction.objectStore('snapshots')
  return {
    pages: transaction.objectStore('pages'),
    snapshots,
    byPage: snapshots.index('byPage'),
    unconfirmed: snapshots.index('byPageAndUnconfirmed'),
    recoveries: transaction.objectStore('recoveries').index('byPage'),
  }
}

/** The pages with neither an unconfirmed snapshot nor a recovery copy, found in the same transaction. */
function findClean(
  stores: CleanupStores,
  candidates: CachedPage[],
  done: (clean: CachedPage[]) => void,
) {
  if (candidates.length === 0) return done([])
  const clean: CachedPage[] = []
  let remaining = candidates.length
  for (const page of candidates) {
    const pending = stores.unconfirmed.count([...pageKey(page), 1])
    const copies = stores.recoveries.count(pageKey(page))
    copies.onsuccess = () => {
      if (pending.result === 0 && copies.result === 0) clean.push(page)
      remaining--
      if (remaining === 0) done(clean)
    }
  }
}

function removePage(stores: CleanupStores, page: CachedPage) {
  stores.pages.delete(pageKey(page))
  const cursor = stores.byPage.openKeyCursor(IDBKeyRange.only(pageKey(page)))
  cursor.onsuccess = () => {
    const entry = cursor.result
    if (!entry) return
    stores.snapshots.delete(entry.primaryKey)
    entry.continue()
  }
}

function pageKeyFields({ userId, workspaceId, pageId }: PageKey): PageKey {
  return { userId, workspaceId, pageId }
}
