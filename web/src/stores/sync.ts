import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type * as Y from 'yjs'
import { useAuthStore } from './auth'
import { useNotificationsStore } from './notifications'
import {
  SyncClient,
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
}

function without(pages: Record<string, PageSync>, pageId: string): Record<string, PageSync> {
  const rest = { ...pages }
  delete rest[pageId]
  return rest
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

  const connection = ref<ConnectionStatus>('connecting')
  const pages = ref<Record<string, PageSync>>({})
  /** Set when the server ended the session, or when connecting keeps failing and the sign-in state says so. */
  const sessionEnded = ref(false)
  let client: SyncClient | null = null

  const live = computed(
    () =>
      connection.value === 'open' &&
      Object.values(pages.value).every((page) => page.state !== 'syncing'),
  )
  const display = computed<SyncDisplay>(() =>
    live.value ? 'live' : connection.value === 'offline' ? 'offline' : 'connecting',
  )

  function snapshot(handle: PageHandle): PageSync {
    return { state: handle.state, readOnly: handle.readOnly, unsent: handle.unsent }
  }

  // Closing the tab with changes that have not reached the server loses them: nothing keeps them across a reload yet.
  function warnBeforeUnload(event: BeforeUnloadEvent) {
    if (!client?.hasUnsent()) return
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

  function ensureClient(): SyncClient {
    if (client) return client
    sessionEnded.value = false
    client = new SyncClient({
      url: syncUrl(window.location),
      onStatus: (status) => {
        connection.value = status
      },
      onPageChange: (pageId) => {
        const handle = client?.get(pageId)
        if (handle) pages.value = { ...pages.value, [pageId]: snapshot(handle) }
        else pages.value = without(pages.value, pageId)
      },
      onSessionEnded: () => void checkSession().then(() => (sessionEnded.value = true)),
      onConnectFailing: () => void checkSession(),
      onError: (code) => notifications.post({ type: 'error', code }),
    })
    window.addEventListener('beforeunload', warnBeforeUnload)
    return client
  }

  /** Starts receiving a page. The same page opened twice gives the same handle. */
  function open(
    pageId: string,
    options: { readOnly?: boolean; init?: (doc: Y.Doc) => void } = {},
  ): PageHandle {
    const handle = ensureClient().subscribe(pageId, options)
    pages.value = { ...pages.value, [pageId]: snapshot(handle) }
    return handle
  }

  /** The person left the page. Changes still on their way keep it open in the background until they are sent. */
  function close(pageId: string) {
    client?.unsubscribe(pageId)
    if (!client?.get(pageId)) pages.value = without(pages.value, pageId)
  }

  /** Ends the socket, when the person leaves. */
  function destroy() {
    client?.destroy()
    client = null
    window.removeEventListener('beforeunload', warnBeforeUnload)
    connection.value = 'connecting'
    pages.value = {}
  }

  return { connection, pages, live, display, sessionEnded, open, close, destroy }
})
