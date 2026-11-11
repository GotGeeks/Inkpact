import { defineStore } from 'pinia'
import { computed, shallowRef } from 'vue'
import { openOfflineDatabase } from '../offline/indexedDb'
import { OfflineDocument, type Durability } from '../offline/offlineDocument'
import type { CachedPage, OfflineRepository, PageKey } from '../offline/types'

const MAX_CLEAN_PAGES = 40
const keyOf = ({ userId, workspaceId, pageId }: PageKey) => `${userId}\0${workspaceId}\0${pageId}`

interface DocumentStatus extends PageKey, Durability {
  active: boolean
}

type MaintenanceSource = 'cleanup' | 'recoveryRead' | 'recoveryDelete'

/**
 * The repository is shared by this tab; each open document still has its own writer. A failed
 * open is not cached, so a later retry can recover after a blocked upgrade or storage failure.
 */
export const useOfflineStore = defineStore('offline', () => {
  const statuses = shallowRef(new Map<symbol, DocumentStatus>())
  const evicted = shallowRef(0)
  const maintenanceErrors = shallowRef(new Map<MaintenanceSource, unknown>())
  const sessions = new Map<string, OfflineDocument>()
  const retained = new Set<OfflineDocument>()
  const opening = new Map<string, Promise<OfflineDocument>>()
  const lifecycleBySession = new WeakMap<OfflineDocument, { token: symbol; active: boolean }>()
  let repositoryPromise: Promise<OfflineRepository> | null = null
  let requestedPersistence = false
  let watchingVisibility = false

  /** A hidden tab may be closed or killed without a warning (a phone does): what waits to be saved is saved now. */
  function watchVisibility() {
    if (watchingVisibility || typeof document === 'undefined') return
    watchingVisibility = true
    const flushAll = () => {
      for (const session of sessions.values()) session.flushNow()
      for (const session of retained) session.flushNow()
    }
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') flushAll()
    })
    window.addEventListener('pagehide', flushAll)
  }

  const hasFailure = computed(
    () =>
      maintenanceErrors.value.size > 0 ||
      [...statuses.value.values()].some((status) => status.error),
  )

  function repository(): Promise<OfflineRepository> {
    if (!repositoryPromise) {
      repositoryPromise = openOfflineDatabase().catch((error: unknown) => {
        repositoryPromise = null
        throw error
      })
    }
    return repositoryPromise
  }

  function setStatus(token: symbol, status: DocumentStatus) {
    const next = new Map(statuses.value)
    if (!status.active && !status.pending && !status.error) next.delete(token)
    else next.set(token, status)
    statuses.value = next
  }

  async function prepare(
    metadata: CachedPage,
    authoritativeMetadata: boolean,
  ): Promise<OfflineDocument> {
    const key = keyOf(metadata)
    const existing = sessions.get(key)
    if (existing) return existing
    const pending = opening.get(key)
    if (pending) return pending
    const token = Symbol(key)
    const lifecycle = { token, active: true }
    const request = (async () => {
      const db = await repository()
      let session: OfflineDocument | null = null
      session = await OfflineDocument.open(db, metadata, authoritativeMetadata, (state) => {
        setStatus(token, { ...metadata, ...state, active: lifecycle.active })
        if (session && !lifecycle.active && !state.pending && !state.error) retained.delete(session)
      })
      sessions.set(key, session)
      lifecycleBySession.set(session, lifecycle)
      watchVisibility()
      if (!requestedPersistence && navigator.storage?.persist) {
        requestedPersistence = true
        void navigator.storage.persist().catch((error: unknown) => {
          console.warn('Persistent browser storage could not be requested', error)
        })
      }
      void cleanup(db)
      return session
    })()
    opening.set(key, request)
    try {
      return await request
    } finally {
      opening.delete(key)
    }
  }

  function release(session: OfflineDocument) {
    const key = keyOf(session.metadata)
    if (sessions.get(key) !== session) return
    sessions.delete(key)
    session.detach()
    const lifecycle = lifecycleBySession.get(session)
    if (lifecycle) {
      lifecycle.active = false
      const current = statuses.value.get(lifecycle.token)
      if (current) {
        if (current.pending || current.error) retained.add(session)
        setStatus(lifecycle.token, { ...current, active: false })
      }
    }
  }

  function statusFor(key: PageKey): Durability | null {
    const all = [...statuses.value.values()].filter((status) => keyOf(status) === keyOf(key))
    if (all.length === 0) return null
    return {
      pending: all.some((status) => status.pending),
      error: all.find((status) => status.error)?.error ?? null,
    }
  }

  function retry(key: PageKey) {
    sessions.get(keyOf(key))?.retry()
    for (const session of retained) if (keyOf(session.metadata) === keyOf(key)) session.retry()
  }

  async function cleanup(db: OfflineRepository) {
    try {
      evicted.value += await db.cleanup(MAX_CLEAN_PAGES)
      clearError('cleanup')
    } catch (error) {
      console.error('Old offline pages could not be cleaned up', error)
      reportError('cleanup', error)
    }
  }

  async function retryAll() {
    for (const session of sessions.values()) session.retry()
    for (const session of retained) session.retry()
    try {
      await cleanup(await repository())
    } catch (error) {
      console.error('Offline storage could not be reopened', error)
      reportError('cleanup', error)
    }
  }

  /**
   * On signing out: the account's pages that the server has are removed from this device, so the next person at the
   * browser does not find them. Pages with unconfirmed edits or recovery copies stay, as removing them would lose work.
   */
  async function forgetAccount(userId: string): Promise<number> {
    for (const session of [...sessions.values(), ...retained]) {
      if (session.metadata.userId !== userId || !session.stopCaching()) continue
      sessions.delete(keyOf(session.metadata))
      retained.delete(session)
    }
    return (await repository()).forgetConfirmed(userId)
  }

  function reportError(source: MaintenanceSource, error: unknown) {
    maintenanceErrors.value = new Map(maintenanceErrors.value).set(source, error)
  }

  function clearError(source: MaintenanceSource) {
    const next = new Map(maintenanceErrors.value)
    next.delete(source)
    maintenanceErrors.value = next
  }

  return {
    statuses,
    evicted,
    hasFailure,
    repository,
    prepare,
    release,
    statusFor,
    retry,
    retryAll,
    forgetAccount,
    reportError,
    clearError,
  }
})
