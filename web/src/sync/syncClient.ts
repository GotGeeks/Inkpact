import * as Y from 'yjs'
import { Awareness, applyAwarenessUpdate, encodeAwarenessUpdate } from 'y-protocols/awareness'
import {
  decodeFrame,
  encodeFrame,
  encodePing,
  MAX_MESSAGE_BYTES,
  MessageType,
  type IncomingFrame,
} from './protocol'
import { remoteOrigin } from '../offline/offlineDocument'

/** The socket, as far as the client uses it: the browser's WebSocket, or a fake in a test. */
export interface SocketLike {
  readonly readyState: number
  binaryType: string
  onopen: ((event: unknown) => void) | null
  onmessage: ((event: { data: unknown }) => void) | null
  onclose: ((event: { code: number; reason: string }) => void) | null
  send(data: Uint8Array): void
  close(code?: number, reason?: string): void
}

/** `connecting`: no socket yet or one still opening. `open`: a socket is open (pages may still be syncing). */
export type ConnectionStatus = 'connecting' | 'open' | 'offline'

/**
 * `syncing`: waiting for the server's answer. `synced`: in step with the server. `missing`: the server has no such page
 * for this person. `tooLarge`: a change is bigger than a message may be, so nothing more is sent for this page.
 */
export type PageState = 'syncing' | 'synced' | 'missing' | 'tooLarge' | 'blocked'

export interface PageHandle {
  readonly pageId: string
  readonly doc: Y.Doc
  readonly awareness: Awareness
  readonly state: PageState
  /** True when the person may not change the page: known beforehand, or told by the server (`read_only`). */
  readonly readOnly: boolean
  /** Local changes the server has not got yet. */
  readonly unsent: boolean
  /** Includes updates sent on the socket but not yet confirmed by a subsequent pong. */
  readonly unconfirmed: boolean
  /**
   * The bytes the server sent with the first Sync of this page (its state, or its stored updates): about how large the page
   * is, known without encoding the document again. 0 until the first Sync.
   */
  readonly firstSyncBytes: number
}

export interface SyncClientOptions {
  url: string
  createSocket?: (url: string, protocols?: string[]) => SocketLike
  /** WebSocket subprotocols to ask for: the account the documents belong to (`syncProtocols`). */
  protocols?: string[]
  onStatus?: (status: ConnectionStatus) => void
  /** Called when anything about a page's handle changed: its state, `readOnly` or `unsent`. */
  onPageChange?: (pageId: string) => void
  /** The server ended the session (close code 4401). The client does not reconnect. */
  onSessionEnded?: () => void
  /** Several connection attempts in a row never opened: the session may be gone, which a refused upgrade does not say. */
  onConnectFailing?: () => void
  /** An error about the connection rather than one page, such as `too_many_pages`. */
  onError?: (code: string, pageId: string) => void
  /**
   * A Sync deleted changes of this client that the server did not have yet: text typed offline into a block that was
   * deleted meanwhile (by another person, or by an import that replaced the page). Yjs deletes such text with its block,
   * so it would be gone without a word. `before` is the page as it was just before, with those changes, as one update.
   * Also when the server replaced the page's whole content (a re-import, a restored version) while this client had any
   * change the server had not confirmed, text or not: the replacement was made without it.
   */
  onEditsLost?: (pageId: string, before: Uint8Array) => void
  /** Recheck the account and page permissions before each socket attempt. */
  beforeConnect?: () => Promise<'allow' | 'retry' | 'stop'>
  /** Recheck each page under the same account before sending its state vector or edits. */
  beforeSubscribe?: (
    pageId: string,
  ) => Promise<'edit' | 'read' | 'blocked' | 'missing' | 'retry' | 'stop'>
  random?: () => number
}

export const SESSION_ENDED = 4401

const MIN_RECONNECT_MS = 250
const MAX_RECONNECT_MS = 5000
const JITTER = 0.2
// A connection that loses the network is not closed by anyone: the socket stays "open" and edits vanish into it. So
// the client pings, and gives up on a connection that has been silent this long.
const PING_INTERVAL_MS = 3000
const DEAD_AFTER_MS = 8000
// A timer that fires this much later than it should was throttled (a hidden tab), not starved by a dead connection.
const THROTTLED_AFTER_MS = PING_INTERVAL_MS * 3
const FAILURES_BEFORE_CHECK = 3
// An empty Yjs update: no structs, empty delete set.
const EMPTY_UPDATE_LENGTH = 2
const SOCKET_OPEN = 1

interface Page extends PageHandle {
  state: PageState
  readOnly: boolean
  unsent: boolean
  firstSyncBytes: number
  /** The editor left, but local changes are still on their way: keep the subscription until they are sent. */
  leaving: boolean
  /**
   * Set when an update was sent: the number of the first ping sent after it. A connection can be dead without anybody
   * knowing (a phone that lost its Wi-Fi), and an update written into it is gone. Once that pong is back, the update was
   * certainly received, because the server reads one socket in order.
   */
  confirmAt: number | null
  /**
   * The server said that the Sync it sends next replaces the page's whole content (`Replaced`). Cleared by that Sync,
   * and by a new subscribe, whose Sync is an ordinary one.
   */
  replacing: boolean
  detach(): void
}

/**
 * One WebSocket per tab, carrying any number of pages. Every tab is its own Yjs client with its own client id; tabs only
 * meet through the server.
 *
 * The rule the whole client rests on: every Sync repairs everything. After any reconnect the client subscribes again with
 * its own state vector, applies what the server sends and sends back what the server lacks, so no single Update frame
 * has to arrive for the documents to converge.
 */
export class SyncClient {
  private socket: SocketLike | null = null
  private status: ConnectionStatus = 'connecting'
  private readonly pages = new Map<string, Page>()
  private readonly options: SyncClientOptions
  private reconnectMs = MIN_RECONNECT_MS
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null
  private heartbeat: ReturnType<typeof setInterval> | null = null
  private lastReceivedAt = 0
  private lastTickAt = 0
  private failures = 0
  private pingsSent = 0
  private pongsReceived = 0
  private ended = false
  private destroyed = false
  private attempt = 0
  private confirmationScheduled = false
  private readonly subscribing = new Set<string>()
  /** Pages whose permission check could not be answered, asked again on their own backoff. */
  private readonly checkRetries = new Map<
    string,
    { timer: ReturnType<typeof setTimeout> | null; ms: number }
  >()

  private readonly onOffline = () => this.dropSocket()
  private readonly onOnline = () => this.reconnectNow()
  private readonly onVisible = () => {
    if (document.visibilityState !== 'visible' || !this.isOpen()) return
    this.lastReceivedAt = Date.now()
    this.sendPing()
  }

  constructor(options: SyncClientOptions) {
    this.options = options
    if (typeof window !== 'undefined') {
      window.addEventListener('offline', this.onOffline)
      window.addEventListener('online', this.onOnline)
      document.addEventListener('visibilitychange', this.onVisible)
    }
    this.open()
  }

  get connection(): ConnectionStatus {
    return this.status
  }

  /** True when a page has changes the server has not got, so closing the tab would lose them. */
  hasUnsent(): boolean {
    for (const page of this.pages.values()) if (page.unconfirmed) return true
    return false
  }

  openPages(): readonly PageHandle[] {
    return [...this.pages.values()]
  }

  /** Called after a fresh server permission check, before a page can subscribe again. */
  setPageAccess(pageId: string, access: 'edit' | 'read' | 'blocked' | 'missing') {
    const page = this.pages.get(pageId)
    if (!page) return
    if (access === 'missing' || access === 'blocked') {
      page.readOnly = true
      this.setState(page, access)
      return
    }
    page.readOnly = access === 'read'
    if (page.state === 'blocked' || page.state === 'missing') this.setState(page, 'syncing')
    this.options.onPageChange?.(pageId)
  }

  /** Stops immediately when another tab changes the account, without destroying local documents. */
  stopForIdentityChange() {
    if (this.destroyed || this.ended) return
    this.ended = true
    this.attempt++
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer)
    this.reconnectTimer = null
    this.closeSocket()
    this.stopHeartbeat()
    this.markAllSyncing()
    this.setStatus('offline')
  }

  get(pageId: string): PageHandle | undefined {
    return this.pages.get(pageId)
  }

  /**
   * Starts receiving a page. The client makes the document, so that it can keep it alive after the editor left; `init`
   * runs once on a new document, before the subscribe, so the state vector sent already holds what it adds. Opening a
   * page that is already open (or still being left) returns the same handle.
   */
  subscribe(
    pageId: string,
    options: {
      readOnly?: boolean
      init?: (doc: Y.Doc) => void
      doc?: Y.Doc
      onDetach?: () => void
      unconfirmed?: boolean
    } = {},
  ): PageHandle {
    const existing = this.pages.get(pageId)
    if (existing) {
      existing.leaving = false
      // A page the server made read-only stays so; asking for read-only on an open page is always allowed.
      if (options.readOnly) existing.readOnly = true
      return existing
    }

    const doc = options.doc ?? new Y.Doc()
    if (!options.doc) options.init?.(doc)
    const awareness = new Awareness(doc)
    // Nobody is announced until a caller says who is here: y-protocols starts every client with an empty state.
    awareness.setLocalState(null)

    const onUpdate = (update: Uint8Array, origin: unknown) => {
      if (origin === remoteOrigin) return
      if (page.readOnly) {
        this.markUnsent(page, true)
        this.setState(page, 'blocked')
        return
      }
      if (page.state === 'missing' || page.state === 'tooLarge' || page.state === 'blocked') {
        this.markUnsent(page, true)
        return
      }
      // Until the page is synced, local changes are held back: the diff sent after the Sync covers them, so nothing
      // reaches the server twice.
      if (page.state !== 'synced' || !this.isOpen()) {
        this.markUnsent(page, true)
        return
      }
      this.sendUpdate(page, update)
    }
    const onAwareness = (
      { added, updated, removed }: { added: number[]; updated: number[]; removed: number[] },
      origin: unknown,
    ) => {
      // The server closes a connection that sends awareness for a page it does not know.
      if (origin === remoteOrigin || page.state !== 'synced') return
      this.send(
        MessageType.Awareness,
        pageId,
        encodeAwarenessUpdate(awareness, [...added, ...updated, ...removed]),
      )
    }
    doc.on('update', onUpdate)
    awareness.on('update', onAwareness)

    const page: Page = {
      pageId,
      doc,
      awareness,
      state: 'syncing',
      readOnly: options.readOnly ?? false,
      unsent: options.unconfirmed ?? false,
      get unconfirmed() {
        return this.unsent || this.confirmAt !== null
      },
      firstSyncBytes: 0,
      leaving: false,
      confirmAt: null,
      replacing: false,
      detach() {
        doc.off('update', onUpdate)
        awareness.off('update', onAwareness)
        awareness.destroy()
        options.onDetach?.()
        doc.destroy()
      },
    }
    this.pages.set(pageId, page)
    this.sendSubscribe(page)
    return page
  }

  /** The person left the page. Changes still on their way keep it subscribed until the next Sync has sent them. */
  unsubscribe(pageId: string) {
    const page = this.pages.get(pageId)
    if (!page) return
    if (page.unconfirmed && (page.state !== 'synced' || !this.isOpen())) {
      page.leaving = true
      return
    }
    if (page.confirmAt !== null && this.isOpen()) {
      // The last update may have gone into a dead connection: wait for a pong that was asked for after it.
      page.leaving = true
      this.sendPing()
      return
    }
    this.release(page)
  }

  destroy() {
    this.destroyed = true
    this.attempt++
    if (typeof window !== 'undefined') {
      window.removeEventListener('offline', this.onOffline)
      window.removeEventListener('online', this.onOnline)
      document.removeEventListener('visibilitychange', this.onVisible)
    }
    this.stopHeartbeat()
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer)
    this.reconnectTimer = null
    for (const retry of this.checkRetries.values()) if (retry.timer) clearTimeout(retry.timer)
    this.checkRetries.clear()
    for (const page of this.pages.values()) page.detach()
    this.pages.clear()
    this.closeSocket()
  }

  // ---- connection ---------------------------------------------------------------------------------------------------

  private open() {
    this.reconnectTimer = null
    if (this.destroyed || this.ended) return
    this.setStatus('connecting')
    const attempt = ++this.attempt
    if (this.options.beforeConnect) {
      void this.options.beforeConnect().then(
        (decision) => {
          if (attempt !== this.attempt || this.destroyed || this.ended) return
          if (decision === 'allow') this.openSocket()
          else if (decision === 'retry') this.connectionLost(false)
          else {
            this.ended = true
            this.setStatus('offline')
          }
        },
        (error: unknown) => {
          if (attempt !== this.attempt || this.destroyed || this.ended) return
          console.error('The sync connection could not be authorized', error)
          this.connectionLost(false)
        },
      )
      return
    }
    this.openSocket()
  }

  private openSocket() {
    let socket: SocketLike
    try {
      socket = (this.options.createSocket ?? defaultSocket)(
        this.options.url,
        this.options.protocols,
      )
    } catch {
      this.connectionLost(false)
      return
    }
    this.socket = socket
    socket.binaryType = 'arraybuffer'

    // Every handler first checks that its socket is still the current one: a dead socket we gave up on may still report
    // a close much later, and that must not disturb the connection that replaced it.
    socket.onopen = () => {
      if (socket !== this.socket) return
      this.failures = 0
      this.pingsSent = 0
      this.pongsReceived = 0
      this.lastReceivedAt = Date.now()
      this.lastTickAt = Date.now()
      this.startHeartbeat()
      this.setStatus('open')
      for (const page of this.pages.values()) this.sendSubscribe(page)
    }
    socket.onmessage = (event) => {
      if (socket !== this.socket) return
      // Any message proves the connection is alive, not only a pong.
      this.lastReceivedAt = Date.now()
      try {
        const frame = decodeFrame(new Uint8Array(event.data as ArrayBuffer))
        if (frame) this.handle(frame)
      } catch (error) {
        // Something we cannot read: the connection is not to be trusted. Starting over resyncs everything.
        console.error('A sync message could not be handled', error)
        this.dropSocket()
      }
    }
    socket.onclose = (event) => {
      if (socket !== this.socket) return
      if (event.code === SESSION_ENDED) {
        this.sessionEnded()
        return
      }
      if (event.code === 1008) console.error('The sync server closed the connection:', event.reason)
      this.connectionLost(this.status === 'open')
    }
  }

  private sessionEnded() {
    this.ended = true
    this.stopHeartbeat()
    this.socket = null
    this.markAllSyncing()
    this.setStatus('offline')
    this.options.onSessionEnded?.()
  }

  private connectionLost(wasOpen: boolean) {
    this.stopHeartbeat()
    this.socket = null
    this.subscribing.clear()
    this.markAllSyncing()
    this.setStatus('offline')
    if (this.destroyed || this.ended) return

    if (!wasOpen) {
      // The server never accepted the socket (a refused upgrade shows up as a plain close): the session may have ended.
      this.failures++
      if (this.failures % FAILURES_BEFORE_CHECK === 0) this.options.onConnectFailing?.()
    }
    if (this.reconnectTimer) return
    const random = this.options.random ?? Math.random
    const delay = this.reconnectMs * (1 + (random() * 2 - 1) * JITTER)
    this.reconnectTimer = setTimeout(() => this.open(), delay)
    this.reconnectMs = Math.min(this.reconnectMs * 2, MAX_RECONNECT_MS)
  }

  /** Gives up on the current socket without waiting for it to report anything. */
  private dropSocket() {
    if (this.destroyed || this.ended) return
    this.attempt++
    if (!this.socket) {
      this.connectionLost(false)
      return
    }
    const wasOpen = this.status === 'open'
    this.closeSocket()
    this.connectionLost(wasOpen)
  }

  /** The network came back: do not wait for the backoff, and do not trust a socket from before the outage. */
  private reconnectNow() {
    if (this.destroyed || this.ended) return
    this.attempt++
    if (this.socket) this.dropSocket()
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer)
    this.reconnectTimer = null
    this.reconnectMs = MIN_RECONNECT_MS
    this.open()
  }

  private closeSocket() {
    const socket = this.socket
    this.socket = null
    this.subscribing.clear()
    if (!socket) return
    socket.onopen = socket.onmessage = socket.onclose = null
    try {
      socket.close()
    } catch {
      // Already gone.
    }
  }

  private startHeartbeat() {
    this.stopHeartbeat()
    this.heartbeat = setInterval(() => {
      const now = Date.now()
      const late = now - this.lastTickAt > THROTTLED_AFTER_MS
      this.lastTickAt = now
      if (late) {
        // The timer slept, not the connection: give it a fresh window and ask.
        this.lastReceivedAt = now
      } else if (now - this.lastReceivedAt > DEAD_AFTER_MS) {
        this.dropSocket()
        return
      }
      this.sendPing()
    }, PING_INTERVAL_MS)
  }

  private stopHeartbeat() {
    if (this.heartbeat) clearInterval(this.heartbeat)
    this.heartbeat = null
  }

  private setStatus(status: ConnectionStatus) {
    if (this.status === status) return
    this.status = status
    this.options.onStatus?.(status)
  }

  private isOpen(): boolean {
    return this.socket?.readyState === SOCKET_OPEN && this.status === 'open'
  }

  // ---- pages --------------------------------------------------------------------------------------------------------

  private markAllSyncing() {
    for (const page of this.pages.values()) {
      // What was sent but never confirmed may be lost: the next sync sends the difference again.
      if (page.confirmAt !== null) {
        page.confirmAt = null
        if (!page.readOnly) this.markUnsent(page, true)
      }
      if (page.state !== 'tooLarge') this.setState(page, 'syncing')
    }
  }

  private setState(page: Page, state: PageState) {
    if (page.state === state) return
    page.state = state
    this.options.onPageChange?.(page.pageId)
  }

  private markUnsent(page: Page, unsent: boolean) {
    if (page.unsent === unsent) return
    page.unsent = unsent
    this.options.onPageChange?.(page.pageId)
  }

  private sendSubscribe(page: Page) {
    if (!this.isOpen() || page.state === 'tooLarge') return
    // Whatever the old connection announced went with it: the Sync that answers this is an ordinary one.
    page.replacing = false
    if (this.options.beforeSubscribe) {
      if (this.subscribing.has(page.pageId)) return
      this.subscribing.add(page.pageId)
      const socket = this.socket
      void this.options.beforeSubscribe(page.pageId).then(
        (access) => {
          if (socket !== this.socket || this.pages.get(page.pageId) !== page) return
          this.subscribing.delete(page.pageId)
          if (access === 'retry') {
            // Only this page waits: the socket and the other pages on it carry on.
            this.retryCheck(page, socket)
            return
          }
          this.checkRetries.delete(page.pageId)
          if (access === 'stop') {
            this.ended = true
            this.closeSocket()
            this.stopHeartbeat()
            this.markAllSyncing()
            this.setStatus('offline')
            return
          }
          this.setPageAccess(page.pageId, access)
          if (access === 'blocked' || access === 'missing') return
          this.send(MessageType.Subscribe, page.pageId, Y.encodeStateVector(page.doc))
        },
        (error: unknown) => {
          if (socket !== this.socket || this.pages.get(page.pageId) !== page) return
          this.subscribing.delete(page.pageId)
          console.error('The page could not be authorized for sync', error)
          this.retryCheck(page, socket)
        },
      )
      return
    }
    // The state vector tells the server what this client already has; it answers with what is missing.
    this.send(MessageType.Subscribe, page.pageId, Y.encodeStateVector(page.doc))
  }

  /**
   * A page's permission check could not be answered (the server or the network failed): it is asked again for that page
   * alone, later, while the socket stays. A new socket asks for every page anyway.
   */
  private retryCheck(page: Page, socket: SocketLike | null) {
    const retry = this.checkRetries.get(page.pageId) ?? { timer: null, ms: MIN_RECONNECT_MS }
    if (retry.timer) clearTimeout(retry.timer)
    const random = this.options.random ?? Math.random
    const delay = retry.ms * (1 + (random() * 2 - 1) * JITTER)
    retry.timer = setTimeout(() => {
      retry.timer = null
      if (socket !== this.socket || this.pages.get(page.pageId) !== page) return
      this.sendSubscribe(page)
    }, delay)
    retry.ms = Math.min(retry.ms * 2, MAX_RECONNECT_MS)
    this.checkRetries.set(page.pageId, retry)
  }

  private release(page: Page) {
    const retry = this.checkRetries.get(page.pageId)
    if (retry?.timer) clearTimeout(retry.timer)
    this.checkRetries.delete(page.pageId)
    this.pages.delete(page.pageId)
    if (page.state === 'synced') {
      // The server announces our presence removal to the remaining peers.
      page.awareness.setLocalState(null)
    }
    if (this.isOpen() && page.state !== 'missing') this.send(MessageType.Unsubscribe, page.pageId)
    page.detach()
    // A page left in the background goes later than the call that left it: whoever shows its state has to hear.
    this.options.onPageChange?.(page.pageId)
  }

  private handle(frame: IncomingFrame) {
    if (frame.type === MessageType.Pong) {
      this.pongsReceived++
      this.confirm()
      return
    }
    const page = this.pages.get(frame.pageId)
    if (!page) return

    switch (frame.type) {
      case MessageType.Sync:
        this.handleSync(page, frame.stateVector, frame.updates)
        break
      case MessageType.Update:
        if (page.state !== 'blocked') Y.applyUpdate(page.doc, frame.payload, remoteOrigin)
        break
      case MessageType.Replaced:
        page.replacing = true
        break
      case MessageType.Awareness:
        applyAwarenessUpdate(page.awareness, frame.payload, remoteOrigin)
        break
      case MessageType.Error:
        this.handleError(page, frame.code)
        break
    }
  }

  private confirm() {
    for (const page of [...this.pages.values()]) {
      if (page.confirmAt === null || this.pongsReceived < page.confirmAt) continue
      page.confirmAt = null
      this.options.onPageChange?.(page.pageId)
      if (page.leaving) this.release(page)
    }
  }

  private handleSync(page: Page, serverStateVector: Uint8Array, updates: Uint8Array[]) {
    if (page.state === 'blocked') return
    if (page.firstSyncBytes === 0)
      page.firstSyncBytes = updates.reduce((sum, update) => sum + update.byteLength, 0)
    let serverVector = serverStateVector
    if (serverStateVector.length === 0) {
      // Log mode: the server's state vector is what a scratch document holding the stored updates has. The first one may
      // be a snapshot, which Yjs takes like any other update.
      const scratch = new Y.Doc()
      for (const update of updates) Y.applyUpdate(scratch, update)
      serverVector = Y.encodeStateVector(scratch)
      scratch.destroy()
    }

    // A Sync that replaces the whole page was made without anything this client has not had confirmed: text, but also
    // formatting or a deletion, which the items below do not show. The server stored nothing of this client between
    // making the replacement and sending it, and answers a socket in order, so "not confirmed" is exactly "not in it".
    const replacing = page.replacing
    const replacedUnderChanges = replacing && !page.readOnly && page.unconfirmed
    page.replacing = false
    // What this client made that the server does not have yet, and the page as it is, in case the Sync deletes some of it.
    const unsynced = page.readOnly ? [] : unsyncedItems(page.doc, serverVector)
    const before =
      unsynced.length > 0 || replacedUnderChanges ? Y.encodeStateAsUpdate(page.doc) : null
    // In diff mode one update, in log mode the stored ones, each in its own apply (one transaction can lose a deletion,
    // run 0.5).
    for (const update of updates) Y.applyUpdate(page.doc, update, remoteOrigin)
    if (before && (replacedUnderChanges || unsynced.some((item) => item.deleted)))
      this.options.onEditsLost?.(page.pageId, before)

    if (page.state === 'tooLarge' || page.state === 'missing') return
    this.setState(page, 'synced')
    // The connection works: only now is the backoff forgotten, so a server that closes straight after opening cannot be
    // hammered at full speed.
    this.reconnectMs = MIN_RECONNECT_MS

    // After a replacement with nothing of this client's on its way, the server lacks nothing. The update below would
    // still not be empty (Yjs writes the whole delete set into every update), and every client that has the page open
    // would store one more row after each replacement, which makes the page look edited to the next import.
    const nothingToSend =
      replacing && !replacedUnderChanges && !hasStructsBeyond(page.doc, serverVector)
    if (!page.readOnly && !nothingToSend) {
      const missing = Y.encodeStateAsUpdate(page.doc, serverVector)
      if (missing.length > EMPTY_UPDATE_LENGTH) this.sendUpdate(page, missing)
    }
    if (page.state === 'synced') this.markUnsent(page, false)

    // Re-announce presence: the server dropped it with the old connection.
    const local = page.awareness.getLocalState()
    if (local !== null) page.awareness.setLocalState(local)

    if (page.leaving) {
      if (page.confirmAt === null) this.release(page)
      else this.sendPing()
    }
  }

  private handleError(page: Page, code: string) {
    switch (code) {
      case 'page_not_found': {
        const rejected = page.unconfirmed
        page.confirmAt = null
        if (rejected) this.markUnsent(page, true)
        this.setState(page, 'missing')
        break
      }
      case 'read_only': {
        // The server stored nothing: the person may read this page but not change it.
        const rejected = page.unconfirmed
        page.confirmAt = null
        page.readOnly = true
        if (rejected) {
          this.markUnsent(page, true)
          this.setState(page, 'blocked')
        }
        this.options.onPageChange?.(page.pageId)
        break
      }
      default:
        this.options.onError?.(code, page.pageId)
    }
  }

  // ---- sending ------------------------------------------------------------------------------------------------------

  /** Sends one update, or stops sending for the page when the message would be over the server's limit. */
  private sendUpdate(page: Page, update: Uint8Array) {
    const frame = encodeFrame(MessageType.Update, page.pageId, update)
    if (frame.length > MAX_MESSAGE_BYTES) {
      // The server would close the connection, and sending the same change again after the reconnect would do it again.
      this.markUnsent(page, true)
      this.setState(page, 'tooLarge')
      return
    }
    if (!this.sendRaw(frame)) {
      this.markUnsent(page, true)
      return
    }
    page.confirmAt = this.pingsSent + 1
    this.options.onPageChange?.(page.pageId)
    if (!this.confirmationScheduled) {
      this.confirmationScheduled = true
      queueMicrotask(() => {
        this.confirmationScheduled = false
        if (!this.destroyed) this.sendPing()
      })
    }
  }

  private sendPing() {
    if (!this.isOpen()) return
    this.pingsSent++
    this.sendRaw(encodePing())
  }

  private send(type: number, pageId: string, payload?: Uint8Array) {
    this.sendRaw(encodeFrame(type, pageId, payload))
  }

  private sendRaw(frame: Uint8Array): boolean {
    if (!this.isOpen()) return false
    try {
      this.socket!.send(frame)
      return true
    } catch (error) {
      console.error('A sync frame could not be sent', error)
      this.dropSocket()
      return false
    }
  }
}

/**
 * The items of the document that the server's state vector does not cover (and that are not deleted already): what this
 * client made while the server could not see it. Only a Sync is checked, where offline edits meet what happened meanwhile;
 * an Update that arrives while online could in theory delete a block someone types into at that very moment, a window
 * of a round trip.
 */
function unsyncedItems(doc: Y.Doc, serverVector: Uint8Array): Y.Item[] {
  const known = Y.decodeStateVector(serverVector)
  const items: Y.Item[] = []
  doc.store.clients.forEach((structs, client) => {
    const clock = known.get(client) ?? 0
    for (let i = structs.length - 1; i >= 0; i--) {
      const struct = structs[i]!
      if (struct.id.clock + struct.length <= clock) break
      // Formatting items are left out: Yjs deletes redundant ones itself when concurrent formatting meets, which loses
      // nothing, and formatting alone is not what a copy would be made for.
      if (
        struct instanceof Y.Item &&
        !struct.deleted &&
        !(struct.content instanceof Y.ContentFormat)
      )
        items.push(struct)
    }
  })
  return items
}

/** Whether the document holds any item, of any client, that the server's state vector does not cover. */
function hasStructsBeyond(doc: Y.Doc, serverVector: Uint8Array): boolean {
  const known = Y.decodeStateVector(serverVector)
  for (const [client, clock] of Y.decodeStateVector(Y.encodeStateVector(doc))) {
    if (clock > (known.get(client) ?? 0)) return true
  }
  return false
}

function defaultSocket(url: string, protocols?: string[]): SocketLike {
  return new WebSocket(url, protocols) as unknown as SocketLike
}

/** The sync socket on the page's own host, `wss:` under HTTPS. */
export function syncUrl(location: Pick<Location, 'protocol' | 'host'>): string {
  return `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/sync`
}

/**
 * The subprotocol that binds the socket to the account whose documents it carries (SyncEndpoints.UserProtocolPrefix
 * on the server): a header, so the id is not in the address a proxy logs.
 */
export function syncProtocols(userId?: string): string[] | undefined {
  return userId ? [`inkpact.user.${userId}`] : undefined
}
