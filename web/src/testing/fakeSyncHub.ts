import * as decoding from 'lib0/decoding'
import * as encoding from 'lib0/encoding'
import * as Y from 'yjs'
import { MessageType } from '../sync/protocol'
import type { SocketLike } from '../sync/syncClient'

/** A stand-in for the server's sync hub, with real Yjs behind it, for testing the client without a network. */

export type HubMode = 'diff' | 'log'

export interface SentFrame {
  type: number
  pageId: string
  payload: Uint8Array
}

const OPEN = 1
const CLOSED = 3

export class FakeSocket implements SocketLike {
  readyState = 0
  binaryType = 'blob'
  onopen: ((event: unknown) => void) | null = null
  onmessage: ((event: { data: unknown }) => void) | null = null
  onclose: ((event: { code: number; reason: string }) => void) | null = null
  /** Pages this connection has subscribed to, as the server sees it. */
  readonly subscribed = new Set<string>()
  /** Messages from the server that have not been delivered yet, while the hub holds them back. */
  held: Uint8Array[] = []
  /** Nothing passes on this connection in either direction, and nobody is told. */
  dead = false

  constructor(readonly hub: FakeSyncHub) {}

  send(data: Uint8Array) {
    if (this.readyState !== OPEN) throw new Error('send on a socket that is not open')
    this.hub.receive(this, data)
  }

  close(code = 1000, reason = '') {
    if (this.readyState === CLOSED) return
    this.readyState = CLOSED
    this.hub.forget(this)
    // A close the client asked for is reported later, like a browser does.
    queueMicrotask(() => this.onclose?.({ code, reason }))
  }

  /** The hub's answer: never in the same turn as the request, as on a real network. */
  reply(data: Uint8Array) {
    queueMicrotask(() => this.deliver(data))
  }

  /** The hub's side: deliver a message to the client now. */
  deliver(data: Uint8Array) {
    if (this.readyState !== OPEN || this.dead) return
    if (this.hub.holding) {
      this.held.push(data)
      return
    }
    this.onmessage?.({
      data: data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength),
    })
  }

  /** The server closes the connection. */
  serverClose(code: number, reason = '') {
    if (this.readyState === CLOSED) return
    this.readyState = CLOSED
    this.hub.forget(this)
    this.onclose?.({ code, reason })
  }
}

export class FakeSyncHub {
  mode: HubMode = 'diff'
  /** New sockets open at once while true; while false they stay connecting. */
  accepting = true
  /** A new socket is refused (closed with 1006 without ever opening). */
  refusing = false
  /** Nothing passes in either direction, and nobody is told: a phone that lost its Wi-Fi. */
  blackholed = false
  /** Messages for the client are kept until `release`. */
  holding = false
  answerPings = true
  /** Page ids that do not exist for this person. */
  readonly missing = new Set<string>()
  /** Page ids the person may read but not change. */
  readonly readOnly = new Set<string>()
  readonly sockets: FakeSocket[] = []
  /** Every frame a client sent, in order, with the socket's index. */
  readonly received: { socket: number; frame: SentFrame }[] = []
  pings = 0
  private nextSocket = 0
  /** How many sockets the client has tried to open. */
  get attempts(): number {
    return this.nextSocket
  }
  private readonly ids = new Map<FakeSocket, number>()
  private readonly docs = new Map<string, Y.Doc>()
  private readonly logs = new Map<string, Uint8Array[]>()

  readonly createSocket = (): SocketLike => {
    const socket = new FakeSocket(this)
    this.ids.set(socket, this.nextSocket++)
    if (this.refusing) {
      queueMicrotask(() => {
        socket.readyState = CLOSED
        socket.onclose?.({ code: 1006, reason: '' })
      })
      return socket
    }
    if (this.accepting) queueMicrotask(() => this.open(socket))
    return socket
  }

  /** Opens a socket that was left connecting. */
  open(socket: FakeSocket) {
    if (socket.readyState !== 0) return
    socket.readyState = OPEN
    this.sockets.push(socket)
    socket.onopen?.({})
  }

  forget(socket: FakeSocket) {
    const at = this.sockets.indexOf(socket)
    if (at >= 0) this.sockets.splice(at, 1)
  }

  /** The newest open socket. */
  get current(): FakeSocket {
    const socket = this.sockets.at(-1)
    if (!socket) throw new Error('no open socket')
    return socket
  }

  socketIndex(socket: FakeSocket): number {
    return this.ids.get(socket)!
  }

  // ---- the server's pages -----------------------------------------------------------------------------------------

  /** What the server holds for a page, as one document. */
  doc(pageId: string): Y.Doc {
    let doc = this.docs.get(pageId)
    if (!doc) {
      doc = new Y.Doc()
      this.docs.set(pageId, doc)
    }
    return doc
  }

  log(pageId: string): Uint8Array[] {
    let log = this.logs.get(pageId)
    if (!log) {
      log = []
      this.logs.set(pageId, log)
    }
    return log
  }

  /** Stores an update the way the real hub does: appended to the log, applied on its own. */
  store(pageId: string, update: Uint8Array) {
    this.log(pageId).push(update)
    Y.applyUpdate(this.doc(pageId), update)
  }

  /** Releases what was held back, in order. */
  release() {
    this.holding = false
    for (const socket of this.sockets) {
      const held = socket.held.splice(0)
      for (const data of held) socket.deliver(data)
    }
  }

  // ---- messages from the client -----------------------------------------------------------------------------------

  receive(socket: FakeSocket, data: Uint8Array) {
    if (this.blackholed || socket.dead) return
    const decoder = decoding.createDecoder(data)
    const type = decoding.readVarUint(decoder)
    if (type === MessageType.Ping) {
      this.pings++
      if (this.answerPings) socket.reply(new Uint8Array([MessageType.Pong]))
      return
    }

    const pageId = decoding.readVarString(decoder)
    const payload =
      type === MessageType.Unsubscribe ? new Uint8Array() : decoding.readVarUint8Array(decoder)
    this.received.push({ socket: this.socketIndex(socket), frame: { type, pageId, payload } })

    switch (type) {
      case MessageType.Subscribe:
        this.subscribe(socket, pageId, payload)
        break
      case MessageType.Unsubscribe:
        socket.subscribed.delete(pageId)
        break
      case MessageType.Update:
        if (!socket.subscribed.has(pageId)) {
          socket.serverClose(1008, 'protocol_error')
          return
        }
        if (this.readOnly.has(pageId)) {
          socket.reply(errorFrame(pageId, 'read_only'))
          return
        }
        this.store(pageId, payload)
        this.relay(socket, pageId, data)
        break
      case MessageType.Awareness:
        if (!socket.subscribed.has(pageId)) {
          socket.serverClose(1008, 'protocol_error')
          return
        }
        this.relay(socket, pageId, data)
        break
    }
  }

  private subscribe(socket: FakeSocket, pageId: string, stateVector: Uint8Array) {
    if (this.missing.has(pageId)) {
      socket.reply(errorFrame(pageId, 'page_not_found'))
      return
    }
    socket.subscribed.add(pageId)
    const log = this.log(pageId)
    if (this.mode === 'log' || log.length === 0) {
      socket.reply(syncFrame(pageId, new Uint8Array(), log))
      return
    }
    const doc = this.doc(pageId)
    socket.reply(
      syncFrame(pageId, Y.encodeStateVector(doc), [Y.encodeStateAsUpdate(doc, stateVector)]),
    )
  }

  private relay(from: FakeSocket, pageId: string, data: Uint8Array) {
    for (const other of this.sockets) {
      if (other !== from && other.subscribed.has(pageId)) other.reply(data)
    }
  }

  /** The frames a client sent of one type, optionally for one page. */
  sent(type: number, pageId?: string): SentFrame[] {
    return this.received
      .map((entry) => entry.frame)
      .filter((frame) => frame.type === type && (pageId === undefined || frame.pageId === pageId))
  }
}

function frameHead(type: number, pageId: string) {
  const encoder = encoding.createEncoder()
  encoding.writeVarUint(encoder, type)
  encoding.writeVarString(encoder, pageId)
  return encoder
}

export function syncFrame(
  pageId: string,
  stateVector: Uint8Array,
  updates: Uint8Array[],
): Uint8Array {
  const encoder = frameHead(MessageType.Sync, pageId)
  encoding.writeVarUint8Array(encoder, stateVector)
  encoding.writeVarUint(encoder, updates.length)
  for (const update of updates) encoding.writeVarUint8Array(encoder, update)
  return encoding.toUint8Array(encoder)
}

export function errorFrame(pageId: string, code: string): Uint8Array {
  const encoder = frameHead(MessageType.Error, pageId)
  encoding.writeVarString(encoder, code)
  return encoding.toUint8Array(encoder)
}

/** A change to a text field, the way an editor makes one, as an update and the document it came from. */
export function textEdit(doc: Y.Doc, text: string, at?: number): Uint8Array {
  let update: Uint8Array = new Uint8Array()
  const listener = (u: Uint8Array) => {
    update = u
  }
  doc.on('update', listener)
  const field = doc.getText('t')
  field.insert(at ?? field.length, text)
  doc.off('update', listener)
  return update
}
