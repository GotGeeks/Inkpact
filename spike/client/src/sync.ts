import * as Y from 'yjs'
import * as encoding from 'lib0/encoding'
import * as decoding from 'lib0/decoding'
import * as syncProtocol from 'y-protocols/sync'

// Same framing as y-websocket: a varuint message type, then the payload.
// Only sync messages for now; awareness (type 1) arrives in run 0.2.
const MESSAGE_SYNC = 0
const RECONNECT_DELAY_MS = 1000

export type Status = 'connecting' | 'connected' | 'disconnected'

/**
 * Syncs a Y.Doc over a relay that only forwards messages between peers.
 * The relay keeps no state, so peers answer each other's sync step 1.
 */
export function connectRelay(doc: Y.Doc, url: string, onStatus: (status: Status) => void) {
  const remote = Symbol('remote')
  let socket: WebSocket

  const send = (write: (encoder: encoding.Encoder) => void) => {
    if (socket.readyState !== WebSocket.OPEN) return
    const encoder = encoding.createEncoder()
    encoding.writeVarUint(encoder, MESSAGE_SYNC)
    write(encoder)
    socket.send(encoding.toUint8Array(encoder))
  }

  let destroyed = false

  // Edits made while offline are not queued: the full-state message on reconnect covers them.
  const onUpdate = (update: Uint8Array, origin: unknown) => {
    if (origin !== remote) send((encoder) => syncProtocol.writeUpdate(encoder, update))
  }
  doc.on('update', onUpdate)

  const open = () => {
    onStatus('connecting')
    socket = new WebSocket(url)
    socket.binaryType = 'arraybuffer'

    socket.onopen = () => {
      onStatus('connected')
      // Ask peers for what we miss...
      send((encoder) => syncProtocol.writeSyncStep1(encoder, doc))
      // ...and push our whole state, since peers never send step 1 back to us.
      // Spike shortcut: this costs a full document per (re)connect; run 0.2 lets the server answer.
      send((encoder) => syncProtocol.writeUpdate(encoder, Y.encodeStateAsUpdate(doc)))
    }

    socket.onmessage = (event: MessageEvent<ArrayBuffer>) => {
      const decoder = decoding.createDecoder(new Uint8Array(event.data))
      if (decoding.readVarUint(decoder) !== MESSAGE_SYNC) return

      const reply = encoding.createEncoder()
      encoding.writeVarUint(reply, MESSAGE_SYNC)
      syncProtocol.readSyncMessage(decoder, reply, doc, remote)
      // readSyncMessage only writes a reply (step 2) when it read a step 1.
      if (encoding.length(reply) > 1) socket.send(encoding.toUint8Array(reply))
    }

    socket.onclose = () => {
      onStatus('disconnected')
      if (!destroyed) setTimeout(open, RECONNECT_DELAY_MS)
    }
  }

  open()

  return {
    destroy() {
      destroyed = true
      doc.off('update', onUpdate)
      socket.close()
    },
  }
}
