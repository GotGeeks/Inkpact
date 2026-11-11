import { defineStore } from 'pinia'
import { computed, ref, shallowRef } from 'vue'
import type * as Y from 'yjs'
import { ApiError, api } from '../api'
import { isOfflineIdentityEvent, readOfflineIdentity } from '../offline/identity'
import type { OfflineDocument } from '../offline/offlineDocument'
import type { CachedRecovery } from '../offline/types'
import type { PageDetail, WorkspaceSummary } from '../tree/types'
import { useAuthStore } from './auth'
import { useNotificationsStore } from './notifications'
import { useOfflineStore } from './offline'
import { useTreeStore } from './tree'
import { newPageId } from '../sync/pageCopy'
import {
  SyncClient,
  syncProtocols,
  syncUrl,
  type ConnectionStatus,
  type PageHandle,
  type PageState,
} from '../sync/syncClient'

/** What the page view needs to know about an open page. Plain data, so Vue can watch it. */
export interface PageSync {
  state: PageState
  readOnly: boolean
  unsent: boolean
  /** About how large the page is, from its first Sync (see <code>PageHandle.firstSyncBytes</code>). */
  firstSyncBytes: number
}

function without(pages: Record<string, PageSync>, pageId: string): Record<string, PageSync> {
  const rest = { ...pages }
  delete rest[pageId]
  return rest
}

/** Edits of this tab that a Sync deleted, as the page was just before: waiting to be kept as a copy (`useKeepLostEdits`). */
export interface LostEdits {
  id: string
  pageId: string
  before: Uint8Array
  userId?: string
  workspaceId?: string
  durable: boolean
  ready: boolean
}

/** `live`: the socket is open and every open page is in step with the server. */
export type SyncDisplay = 'connecting' | 'live' | 'offline'

/**
 * The tab's one sync socket, made when the first page opens and ended when the person leaves. The client itself knows
 * nothing of Vue: this store turns its callbacks into refs.
 */
export const useSyncStore = defineStore('sync', () => {
  const auth = useAuthStore()
  const notifications = useNotificationsStore()
  const offline = useOfflineStore()
  const tree = useTreeStore()

  const connection = ref<ConnectionStatus>('connecting')
  const pages = ref<Record<string, PageSync>>({})
  /** Set when the server ended the session, or when connecting keeps failing and the sign-in state says so. */
  const sessionEnded = ref(false)
  const accountChanged = ref(false)
  const lostEdits = ref<LostEdits[]>([])
  let client: SyncClient | null = null
  let generation = 0
  let clientUserId: string | null = null
  const documents = shallowRef(new Map<string, OfflineDocument>())
  const loadedRecoveries = new Set<string>()
  const failedRecoveryDeletes = new Set<string>()

  const live = computed(
    () =>
      connection.value === 'open' &&
      Object.values(pages.value).every(
        (page) => page.state !== 'syncing' && page.state !== 'blocked',
      ),
  )
  /** Some open page has changes the server has not confirmed yet. */
  const unsent = computed(() => Object.values(pages.value).some((page) => page.unsent))
  const display = computed<SyncDisplay>(() =>
    live.value ? 'live' : connection.value === 'offline' ? 'offline' : 'connecting',
  )
  const unsafeToLeave = computed(() => {
    if (lostEdits.value.some((lost) => !lost.durable)) return true
    return Object.entries(pages.value).some(([pageId, page]) => {
      if (!page.unsent) return false
      const document = documents.value.get(pageId)
      const status = document && offline.statusFor(document.metadata)
      return !status || status.pending || !!status.error
    })
  })

  function snapshot(handle: PageHandle): PageSync {
    return {
      state: handle.state,
      readOnly: handle.readOnly,
      unsent: handle.unconfirmed,
      firstSyncBytes: handle.firstSyncBytes,
    }
  }

  function warnBeforeUnload(event: BeforeUnloadEvent) {
    if (!unsafeToLeave.value) return
    event.preventDefault()
    event.returnValue = ''
  }

  /** Asks the server who is signed in. A server that cannot be reached says nothing about the session. */
  async function checkSession() {
    try {
      await auth.load()
    } catch {
      return
    }
    if (!auth.user) sessionEnded.value = true
  }

  function identityChanged() {
    if (accountChanged.value) return
    client?.stopForIdentityChange()
    if (client && !unsent.value && !unsafeToLeave.value && lostEdits.value.length === 0) {
      sessionEnded.value = true
      return
    }
    accountChanged.value = true
  }

  function onStorage(event: StorageEvent) {
    if (!clientUserId || !isOfflineIdentityEvent(event)) return
    if (readOfflineIdentity() !== clientUserId) identityChanged()
  }

  async function authorizeConnection(
    expected: string,
    mine: number,
  ): Promise<'allow' | 'retry' | 'stop'> {
    try {
      const state = await auth.readState()
      if (mine !== generation || accountChanged.value) return 'stop'
      const remembered = readOfflineIdentity()
      if (
        !state.user ||
        state.user.id !== expected ||
        (remembered !== null && remembered !== expected)
      ) {
        identityChanged()
        return 'stop'
      }
      auth.acceptState(state)
      return 'allow'
    } catch (error) {
      if (!(error instanceof ApiError) || error.code !== 'network_error')
        console.error('The sync account could not be checked', error)
      return 'retry'
    }
  }

  /** The pages checked after one reconnect (the open one, others still sending) share one read of the workspaces. */
  let workspacesRead: { expected: string; request: Promise<WorkspaceSummary[]> } | null = null
  function readWorkspaces(expected: string): Promise<WorkspaceSummary[]> {
    if (workspacesRead?.expected === expected) return workspacesRead.request
    const request = api<WorkspaceSummary[]>(
      'GET',
      '/api/workspaces',
      undefined,
      undefined,
      expected,
    ).finally(() => {
      if (workspacesRead?.request === request) workspacesRead = null
    })
    workspacesRead = { expected, request }
    return request
  }

  async function authorizePage(
    expected: string,
    mine: number,
    pageId: string,
  ): Promise<'edit' | 'read' | 'blocked' | 'missing' | 'retry' | 'stop'> {
    try {
      const [detail, workspaces] = await Promise.all([
        api<PageDetail>(
          'GET',
          `/api/pages/${encodeURIComponent(pageId)}`,
          undefined,
          undefined,
          expected,
        ),
        readWorkspaces(expected),
      ])
      if (mine !== generation || accountChanged.value) return 'stop'
      const workspace = workspaces.find((item) => item.id === detail.workspaceId)
      if (!workspace) return 'missing'
      if (tree.workspace?.id === workspace.id) tree.workspace = workspace
      const document = documents.value.get(pageId)
      const canEdit = workspace.role === 'owner' || workspace.role === 'editor'
      document?.refreshMetadata({
        ...document.metadata,
        title: detail.page.title,
        inTrash: detail.inTrash,
        ownTrash: detail.page.deletedAt !== undefined,
        canEdit,
      })
      if (canEdit) return 'edit'
      return document?.unconfirmed || client?.get(pageId)?.unconfirmed ? 'blocked' : 'read'
    } catch (error) {
      if (mine !== generation) return 'stop'
      if (error instanceof ApiError && error.code === 'session_changed') {
        identityChanged()
        return 'stop'
      }
      if (error instanceof ApiError && (error.status === 403 || error.status === 404))
        return 'missing'
      if (!(error instanceof ApiError) || error.code !== 'network_error')
        console.error('The page permission could not be checked', error)
      return 'retry'
    }
  }

  function rememberLostEdits(pageId: string, before: Uint8Array) {
    const id = newPageId()
    const document = documents.value.get(pageId)
    const userId = document?.metadata.userId ?? clientUserId ?? undefined
    const workspaceId = document?.metadata.workspaceId
    const lost: LostEdits = {
      id,
      pageId,
      before,
      userId,
      workspaceId,
      durable: false,
      ready: !document,
    }
    lostEdits.value = [...lostEdits.value, lost]
    if (!document || !userId || !workspaceId) return
    const recovery: CachedRecovery = { id, pageId, userId, workspaceId, before }
    void document.protect(recovery).then(() => {
      lostEdits.value = lostEdits.value.map((item) =>
        item.id === id ? { ...item, durable: true, ready: true } : item,
      )
    })
  }

  /** Recovers copies left by a previous tab, but only from the currently verified account. */
  async function loadRecoveries(userId: string) {
    if (loadedRecoveries.has(userId)) return
    try {
      const db = await offline.repository()
      const recoveries = await db.listRecoveries(userId)
      if ((auth.user?.id ?? auth.offlineUserId) !== userId) return
      const known = new Set(lostEdits.value.map((lost) => lost.id))
      lostEdits.value = [
        ...lostEdits.value,
        ...recoveries
          .filter((recovery) => !known.has(recovery.id))
          .map((recovery) => ({ ...recovery, durable: true, ready: true })),
      ]
      loadedRecoveries.add(userId)
      offline.clearError('recoveryRead')
      if (recoveries.length > 0) ensureClient()
    } catch (error) {
      console.error('Offline recovery copies could not be loaded', error)
      offline.reportError('recoveryRead', error)
    }
  }

  function ensureClient(): SyncClient {
    if (client) return client
    sessionEnded.value = false
    accountChanged.value = false
    clientUserId = auth.user?.id ?? auth.offlineUserId
    const expected = clientUserId
    const mine = ++generation
    client = new SyncClient({
      url: syncUrl(window.location),
      protocols: syncProtocols(expected ?? undefined),
      beforeConnect: expected ? () => authorizeConnection(expected, mine) : undefined,
      beforeSubscribe: expected ? (pageId) => authorizePage(expected, mine, pageId) : undefined,
      onStatus: (status) => {
        connection.value = status
      },
      onPageChange: (pageId) => {
        const handle = client?.get(pageId)
        if (handle?.state === 'synced') {
          const document = documents.value.get(pageId)
          document?.markReady()
          if (!handle.unconfirmed) document?.markConfirmed()
        }
        if (handle) pages.value = { ...pages.value, [pageId]: snapshot(handle) }
        else pages.value = without(pages.value, pageId)
      },
      onSessionEnded: () => void checkSession().then(() => (sessionEnded.value = true)),
      onConnectFailing: expected ? undefined : () => void checkSession(),
      onError: (code) => notifications.post({ type: 'error', code }),
      onEditsLost: rememberLostEdits,
    })
    window.addEventListener('beforeunload', warnBeforeUnload)
    window.addEventListener('storage', onStorage)
    return client
  }

  /** Starts receiving a page. The same page opened twice gives the same handle. */
  function open(
    pageId: string,
    options: { readOnly?: boolean; init?: (doc: Y.Doc) => void; document?: OfflineDocument } = {},
  ): PageHandle {
    const syncClient = ensureClient()
    const document = options.document
    const existing = syncClient.get(pageId)
    if (document && existing && existing.doc !== document.doc)
      document.adopt(existing.doc, existing.unconfirmed)
    if (document) documents.value = new Map(documents.value).set(pageId, document)
    const handle = syncClient.subscribe(pageId, {
      readOnly: options.readOnly,
      init: options.init,
      doc: document?.doc,
      unconfirmed: document?.unconfirmed,
      onDetach: () => {
        const attached = documents.value.get(pageId)
        if (!attached) return
        const next = new Map(documents.value)
        next.delete(pageId)
        documents.value = next
        offline.release(attached)
      },
    })
    pages.value = { ...pages.value, [pageId]: snapshot(handle) }
    return handle
  }

  /** The person left the page. Changes still on their way keep it open in the background until they are sent. */
  function close(pageId: string) {
    client?.unsubscribe(pageId)
    if (!client?.get(pageId)) pages.value = without(pages.value, pageId)
  }

  function localStatus(pageId: string) {
    const document = documents.value.get(pageId)
    return document ? offline.statusFor(document.metadata) : null
  }

  function ownsDocument(document: OfflineDocument) {
    return documents.value.get(document.metadata.pageId) === document
  }

  async function forgetLostEdits(id: string): Promise<boolean> {
    const lost = lostEdits.value.find((item) => item.id === id)
    if (!lost) return true
    if (lost.durable) {
      try {
        await (await offline.repository()).deleteRecovery(id)
        failedRecoveryDeletes.delete(id)
        if (failedRecoveryDeletes.size === 0) offline.clearError('recoveryDelete')
      } catch (error) {
        console.error('The completed offline recovery could not be cleared', error)
        failedRecoveryDeletes.add(id)
        offline.reportError('recoveryDelete', error)
        return false
      }
    }
    lostEdits.value = lostEdits.value.filter((lost) => lost.id !== id)
    return true
  }

  function retryRecoveries() {
    lostEdits.value = [...lostEdits.value]
  }

  /** Ends the socket, when the person leaves. */
  function destroy() {
    client?.destroy()
    client = null
    generation++
    clientUserId = null
    documents.value = new Map()
    loadedRecoveries.clear()
    failedRecoveryDeletes.clear()
    // The person is gone: what waited to be kept is theirs, and must not be written for whoever signs in next.
    lostEdits.value = []
    window.removeEventListener('beforeunload', warnBeforeUnload)
    window.removeEventListener('storage', onStorage)
    connection.value = 'connecting'
    pages.value = {}
    accountChanged.value = false
    sessionEnded.value = false
  }

  return {
    connection,
    pages,
    live,
    display,
    unsent,
    sessionEnded,
    accountChanged,
    identityChanged,
    lostEdits,
    unsafeToLeave,
    loadRecoveries,
    retryRecoveries,
    localStatus,
    ownsDocument,
    open,
    close,
    forgetLostEdits,
    destroy,
  }
})
