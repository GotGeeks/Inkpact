import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { CachedPage, CachedSnapshot, OfflineRepository } from '../offline/types'
import { OfflineDocument } from '../offline/offlineDocument'
import { useOfflineStore } from './offline'

const repository: OfflineRepository = {
  findPage: async () => null,
  readSnapshots: async (): Promise<CachedSnapshot[]> => [],
  saveSnapshot: async () => {},
  saveRecovery: async () => {},
  listRecoveries: async () => [],
  deleteRecovery: async () => {},
  cleanup: async () => 0,
  forgetConfirmed: async () => 0,
}
vi.mock('../offline/indexedDb', () => ({ openOfflineDatabase: async () => repository }))

const page: CachedPage = {
  userId: 'user-a',
  workspaceId: 'workspace-a',
  pageId: 'page-a',
  title: 'Page',
  inTrash: false,
  ownTrash: false,
  canEdit: true,
  ready: false,
  lastOpened: 1,
}

let visibility: DocumentVisibilityState = 'visible'

beforeEach(() => {
  setActivePinia(createPinia())
  visibility = 'visible'
  vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('the offline store', () => {
  it('writes what waits to be saved when the tab is hidden or left, as a phone may close it then', async () => {
    const offline = useOfflineStore()
    const session = await offline.prepare(page, true)
    const flushNow = vi.spyOn(OfflineDocument.prototype, 'flushNow')

    document.dispatchEvent(new Event('visibilitychange'))
    expect(flushNow).not.toHaveBeenCalled()

    visibility = 'hidden'
    document.dispatchEvent(new Event('visibilitychange'))
    expect(flushNow).toHaveBeenCalledTimes(1)

    window.dispatchEvent(new Event('pagehide'))
    expect(flushNow).toHaveBeenCalledTimes(2)
    offline.release(session)
    session.doc.destroy()
  })

  it('removes a signed-out account from this device, but not its unconfirmed pages', async () => {
    const offline = useOfflineStore()
    const forget = vi.spyOn(repository, 'forgetConfirmed').mockResolvedValue(2)
    const clean = await offline.prepare(page, true)
    const pending = await offline.prepare({ ...page, pageId: 'page-b' }, true)
    pending.doc.getText('t').insert(0, 'unsent')
    const other = await offline.prepare({ ...page, userId: 'user-b' }, true)

    expect(await offline.forgetAccount('user-a')).toBe(2)

    expect(forget).toHaveBeenCalledExactlyOnceWith('user-a')
    // The clean page is no longer this store's, the pending one and the other account's still are.
    expect(await offline.prepare(page, true)).not.toBe(clean)
    expect(await offline.prepare({ ...page, pageId: 'page-b' }, true)).toBe(pending)
    expect(await offline.prepare({ ...page, userId: 'user-b' }, true)).toBe(other)
  })
})
