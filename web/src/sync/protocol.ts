import * as decoding from 'lib0/decoding'
import * as encoding from 'lib0/encoding'

/** Message types of the sync socket. Mirrors `server/src/Inkpact.Api/Sync/Frame.cs`. */
export const MessageType = {
  Subscribe: 0,
  Unsubscribe: 1,
  Update: 2,
  Sync: 3,
  Awareness: 4,
  /** The whole message is this one byte: the client asks whether the connection is alive. */
  Ping: 5,
  /** The whole message is this one byte: the answer to a ping. */
  Pong: 6,
  Error: 7,
} as const

/** The server closes a message over this size (`Sync:MaxMessageBytes`, 2 MiB) with code 1009. */
export const MAX_MESSAGE_BYTES = 2 * 1024 * 1024

export type IncomingFrame =
  | {
      type: typeof MessageType.Update | typeof MessageType.Awareness
      pageId: string
      payload: Uint8Array
    }
  | {
      type: typeof MessageType.Sync
      pageId: string
      /** The server's state vector in diff mode; empty in log mode. */
      stateVector: Uint8Array
      updates: Uint8Array[]
    }
  | { type: typeof MessageType.Error; pageId: string; code: string }
  | { type: typeof MessageType.Pong }

export function encodeFrame(
  type: number,
  pageId: string,
  payload?: Uint8Array,
): Uint8Array<ArrayBuffer> {
  const encoder = encoding.createEncoder()
  encoding.writeVarUint(encoder, type)
  encoding.writeVarString(encoder, pageId)
  if (payload) encoding.writeVarUint8Array(encoder, payload)
  // lib0 always allocates a plain ArrayBuffer; WebSocket.send needs to know that.
  return encoding.toUint8Array(encoder) as Uint8Array<ArrayBuffer>
}

export function encodePing(): Uint8Array<ArrayBuffer> {
  return new Uint8Array([MessageType.Ping])
}

/**
 * Reads one message from the server. Returns null for a type a client does not know, so a newer server does not break
 * an open tab; throws for a message that does not decode, which means the connection is broken.
 */
export function decodeFrame(data: Uint8Array): IncomingFrame | null {
  const decoder = decoding.createDecoder(data)
  const type = decoding.readVarUint(decoder)
  if (type === MessageType.Pong) return { type }
  if (
    type !== MessageType.Update &&
    type !== MessageType.Awareness &&
    type !== MessageType.Sync &&
    type !== MessageType.Error
  ) {
    return null
  }

  const pageId = decoding.readVarString(decoder)
  switch (type) {
    case MessageType.Update:
    case MessageType.Awareness:
      return { type, pageId, payload: decoding.readVarUint8Array(decoder) }
    case MessageType.Sync: {
      const stateVector = decoding.readVarUint8Array(decoder)
      const count = decoding.readVarUint(decoder)
      const updates: Uint8Array[] = []
      for (let i = 0; i < count; i++) updates.push(decoding.readVarUint8Array(decoder))
      return { type, pageId, stateVector, updates }
    }
    case MessageType.Error:
      return { type, pageId, code: decoding.readVarString(decoder) }
  }
}
