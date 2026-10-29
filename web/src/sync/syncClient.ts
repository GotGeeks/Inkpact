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
export type PageState = 'syncing' | 'synced' | 'missing' | 'tooLarge'

export interface PageHandle {
  readonly pageId: string
  readonly doc: Y.Doc
  readonly awareness: Awareness
  readonly state: PageState
  /** True when the person may not change the page: known beforehand, or told by the server (`read_only`). */
  readonly readOnly: boolean
  /** Local changes the server has not got yet. */
  readonly unsent: boolean
}

export interface SyncClientOptions {
  url: string
  createSocket?: (url: string) => SocketLike
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
   */
  onEditsLost?: (pageId: string, before: Uint8Array) => void
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
  /** The editor left, but local changes are still on their way: keep the subscription until they are sent. */
  leaving: boolean
  /**
   * Set when an update was sent: the number of the first ping sent after it. A connection can be dead without anybody
   * knowing (a phone that lost its Wi-Fi), and an update written into it is gone. Once that pong is back, the update was
   * certainly received, because the server reads one socket in order.
   */
  confirmAt: number | null
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
  private readonly remote = Symbol('remote')
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
    for (const page of this.pages.values()) if (page.unsent && !page.readOnly) return true
    return false
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
    options: { readOnly?: boolean; init?: (doc: Y.Doc) => void } = {},
  ): PageHandle {
    const existing = this.pages.get(pageId)
    if (existing) {
      existing.leaving = false
      // A page the server made read-only stays so; asking for read-only on an open page is always allowed.
      if (options.readOnly) existing.readOnly = true
      return existing
    }

    const doc = new Y.Doc()
    options.init?.(doc)
    const awareness = new Awareness(doc)
    // Nobody is announced until a caller says who is here: y-protocols starts every client with an empty state.
    awareness.setLocalState(null)

    const onUpdate = (update: Uint8Array, origin: unknown) => {
      if (origin === this.remote || page.readOnly) return
      if (page.state === 'missing' || page.state === 'tooLarge') return
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
      if (origin === this.remote || page.state !== 'synced') return
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
      unsent: false,
      leaving: false,
      confirmAt: null,
      detach() {
        doc.off('update', onUpdate)
        awareness.off('update', onAwareness)
        awareness.destroy()
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
    if (page.unsent && !page.readOnly && page.state === 'syncing') {
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
    if (typeof window !== 'undefined') {
      window.removeEventListener('offline', this.onOffline)
      window.removeEventListener('online', this.onOnline)
      document.removeEventListener('visibilitychange', this.onVisible)
    }
    this.stopHeartbeat()
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer)
    this.reconnectTimer = null
    for (const page of this.pages.values()) page.detach()
    this.pages.clear()
    this.closeSocket()
  }

  // ---- connection ---------------------------------------------------------------------------------------------------

  private open() {
    this.reconnectTimer = null
    if (this.destroyed || this.ended) return
    this.setStatus('connecting')

    let socket: SocketLike
    try {
      socket = (this.options.createSocket ?? defaultSocket)(this.options.url)
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
    if (this.destroyed || this.ended || !this.socket) return
    const wasOpen = this.status === 'open'
    this.closeSocket()
    this.connectionLost(wasOpen)
  }

  /** The network came back: do not wait for the backoff, and do not trust a socket from before the outage. */
  private reconnectNow() {
    if (this.destroyed || this.ended) return
    if (this.socket) this.dropSocket()
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer)
    this.reconnectTimer = null
    this.reconnectMs = MIN_RECONNECT_MS
    this.open()
  }

  private closeSocket() {
    const socket = this.socket
    this.socket = null
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
      if (page.state === 'synced') this.setState(page, 'syncing')
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
    if (!this.isOpen()) return
    // The state vector tells the server what this client already has; it answers with what is missing.
    this.send(MessageType.Subscribe, page.pageId, Y.encodeStateVector(page.doc))
  }

  private release(page: Page) {
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
        Y.applyUpdate(page.doc, frame.payload, this.remote)
        break
      case MessageType.Awareness:
        applyAwarenessUpdate(page.awareness, frame.payload, this.remote)
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
      if (page.leaving) this.release(page)
    }
  }

  private handleSync(page: Page, serverStateVector: Uint8Array, updates: Uint8Array[]) {
    let serverVector = serverStateVector
    if (serverStateVector.length === 0) {
      // Log mode: the server's state vector is what a scratch document holding the stored updates has. The first one may
      // be a snapshot, which Yjs takes like any other update.
      const scratch = new Y.Doc()
      for (const update of updates) Y.applyUpdate(scratch, update)
      serverVector = Y.encodeStateVector(scratch)
      scratch.destroy()
    }

    // What this client made that the server does not have yet, and the page as it is, in case the Sync deletes some of it.
    const unsynced = page.readOnly ? [] : unsyncedItems(page.doc, serverVector)
    const before = unsynced.length > 0 ? Y.encodeStateAsUpdate(page.doc) : null
    // In diff mode one update, in log mode the stored ones, each in its own apply (one transaction can lose a deletion,
    // run 0.5).
    for (const update of updates) Y.applyUpdate(page.doc, update, this.remote)
    if (before && unsynced.some((item) => item.deleted))
      this.options.onEditsLost?.(page.pageId, before)

    if (page.state === 'tooLarge' || page.state === 'missing') return
    this.setState(page, 'synced')
    // The connection works: only now is the backoff forgotten, so a server that closes straight after opening cannot be
    // hammered at full speed.
    this.reconnectMs = MIN_RECONNECT_MS

    if (!page.readOnly) {
      const missing = Y.encodeStateAsUpdate(page.doc, serverVector)
      if (missing.length > EMPTY_UPDATE_LENGTH) this.sendUpdate(page, missing)
    }
    if (page.state === 'synced') this.markUnsent(page, false)

    // Re-announce presence: the server dropped it with the old connection.
    const local = page.awareness.getLocalState()
    if (local !== null) page.awareness.setLocalState(local)

    if (page.leaving) this.release(page)
  }

  private handleError(page: Page, code: string) {
    switch (code) {
      case 'page_not_found':
        this.setState(page, 'missing')
        break
      case 'read_only':
        // The server stored nothing: the person may read this page but not change it.
        page.readOnly = true
        this.options.onPageChange?.(page.pageId)
        break
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
      this.setState(page, 'tooLarge')
      return
    }
    this.sendRaw(frame)
    page.confirmAt = this.pingsSent + 1
  }

  private sendPing() {
    if (!this.isOpen()) return
    this.pingsSent++
    this.sendRaw(encodePing())
  }

  private send(type: number, pageId: string, payload?: Uint8Array) {
    this.sendRaw(encodeFrame(type, pageId, payload))
  }

  private sendRaw(frame: Uint8Array) {
    if (!this.isOpen()) return
    try {
      this.socket!.send(frame)
    } catch {
      // The socket is closing: its close event starts the reconnect.
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
      if (struct instanceof Y.Item && !struct.deleted) items.push(struct)
    }
  })
  return items
}

function defaultSocket(url: string): SocketLike {
  return new WebSocket(url) as unknown as SocketLike
}

/** The sync socket on the page's own host, `wss:` under HTTPS. */
export function syncUrl(location: Pick<Location, 'protocol' | 'host'>): string {
  return `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/sync`
}
