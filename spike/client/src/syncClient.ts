import * as Y from 'yjs'
import { Awareness, applyAwarenessUpdate, encodeAwarenessUpdate } from 'y-protocols/awareness'
import { MessageType, decodeFrame, encodeFrame, type IncomingFrame } from './protocol.ts'

export type Status = 'connecting' | 'connected' | 'disconnected'

export interface PageHandle {
  readonly doc: Y.Doc
  readonly awareness: Awareness
  readonly synced: boolean
}

interface PageState extends PageHandle {
  synced: boolean
  detach(): void
}

const MIN_RECONNECT_MS = 250
const MAX_RECONNECT_MS = 5000
// An empty Yjs update: no structs, empty delete set.
const EMPTY_UPDATE_LENGTH = 2

/**
 * One WebSocket per session, carrying any number of pages.
 * Every tab is its own Yjs client with a fresh clientID; tabs only meet through the server.
 */
export class SyncClient {
  private socket!: WebSocket
  private readonly pages = new Map<string, PageState>()
  private readonly remote = Symbol('remote')
  private readonly url: string
  private readonly onStatus: (status: Status) => void
  private reconnectMs = MIN_RECONNECT_MS
  private destroyed = false

  constructor(url: string, onStatus: (status: Status) => void = () => {}) {
    this.url = url
    this.onStatus = onStatus
    this.open()
  }

  subscribe(pageId: string, doc = new Y.Doc()): PageHandle {
    const existing = this.pages.get(pageId)
    if (existing) return existing

    const awareness = new Awareness(doc)

    // Until the page is synced, local edits are held back: the diff sent after SYNC covers them,
    // so nothing reaches the server twice.
    const onUpdate = (update: Uint8Array, origin: unknown) => {
      if (origin !== this.remote && state.synced) this.send(MessageType.Update, pageId, update)
    }
    const onAwareness = (
      { added, updated, removed }: { added: number[]; updated: number[]; removed: number[] },
      origin: unknown,
    ) => {
      if (origin === this.remote) return
      const changed = [...added, ...updated, ...removed]
      this.send(MessageType.Awareness, pageId, encodeAwarenessUpdate(awareness, changed))
    }

    doc.on('update', onUpdate)
    awareness.on('update', onAwareness)

    const state: PageState = {
      doc,
      awareness,
      synced: false,
      detach() {
        doc.off('update', onUpdate)
        awareness.off('update', onAwareness)
        awareness.destroy()
      },
    }
    this.pages.set(pageId, state)
    this.send(MessageType.Subscribe, pageId)
    return state
  }

  unsubscribe(pageId: string) {
    const state = this.pages.get(pageId)
    if (!state) return
    this.pages.delete(pageId)
    state.detach()
    // The server announces our presence removal to the remaining peers.
    this.send(MessageType.Unsubscribe, pageId)
  }

  destroy() {
    this.destroyed = true
    for (const state of this.pages.values()) state.detach()
    this.pages.clear()
    this.socket.close()
  }

  private open() {
    this.onStatus('connecting')
    this.socket = new WebSocket(this.url)
    this.socket.binaryType = 'arraybuffer'

    this.socket.onopen = () => {
      this.reconnectMs = MIN_RECONNECT_MS
      this.onStatus('connected')
      for (const pageId of this.pages.keys()) this.send(MessageType.Subscribe, pageId)
    }

    this.socket.onmessage = (event: MessageEvent<ArrayBuffer>) => {
      const frame = decodeFrame(new Uint8Array(event.data))
      if (frame) this.handle(frame)
    }

    this.socket.onclose = () => {
      for (const state of this.pages.values()) state.synced = false
      this.onStatus('disconnected')
      if (this.destroyed) return
      setTimeout(() => this.open(), this.reconnectMs)
      this.reconnectMs = Math.min(this.reconnectMs * 2, MAX_RECONNECT_MS)
    }
  }

  private handle(frame: IncomingFrame) {
    const state = this.pages.get(frame.pageId)
    if (!state) return

    switch (frame.type) {
      case MessageType.Sync: {
        // The server holds opaque updates and cannot diff, so the client works out what it misses.
        const merged = Y.mergeUpdates(frame.updates)
        const serverStateVector = Y.encodeStateVectorFromUpdate(merged)
        Y.applyUpdate(state.doc, merged, this.remote)

        const missing = Y.encodeStateAsUpdate(state.doc, serverStateVector)
        if (missing.length > EMPTY_UPDATE_LENGTH) this.send(MessageType.Update, frame.pageId, missing)
        state.synced = true

        // Re-announce presence: the server dropped it when the old connection went away.
        if (state.awareness.getLocalState() !== null) {
          state.awareness.setLocalState(state.awareness.getLocalState())
        }
        break
      }
      case MessageType.Update:
        Y.applyUpdate(state.doc, frame.payload, this.remote)
        break
      case MessageType.Awareness:
        applyAwarenessUpdate(state.awareness, frame.payload, this.remote)
        break
    }
  }

  private send(type: number, pageId: string, payload?: Uint8Array) {
    if (this.socket.readyState === WebSocket.OPEN) this.socket.send(encodeFrame(type, pageId, payload))
  }
}
