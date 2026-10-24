import * as encoding from 'lib0/encoding'
import * as decoding from 'lib0/decoding'

// Every frame: [varuint type][varstring pageId][payload]. Mirrors Spike.Server/Protocol/Frame.cs.
export const MessageType = {
  Subscribe: 0,
  Unsubscribe: 1,
  Update: 2,
  Sync: 3,
  Awareness: 4,
} as const

export type IncomingFrame =
  | { type: typeof MessageType.Update | typeof MessageType.Awareness; pageId: string; payload: Uint8Array }
  | { type: typeof MessageType.Sync; pageId: string; updates: Uint8Array[] }

export function encodeFrame(type: number, pageId: string, payload?: Uint8Array): Uint8Array<ArrayBuffer> {
  const encoder = encoding.createEncoder()
  encoding.writeVarUint(encoder, type)
  encoding.writeVarString(encoder, pageId)
  if (payload) encoding.writeVarUint8Array(encoder, payload)
  // lib0 always allocates a plain ArrayBuffer; WebSocket.send needs to know that.
  return encoding.toUint8Array(encoder) as Uint8Array<ArrayBuffer>
}

/** Returns null for frame types a client does not expect from the server. */
export function decodeFrame(data: Uint8Array): IncomingFrame | null {
  const decoder = decoding.createDecoder(data)
  const type = decoding.readVarUint(decoder)
  const pageId = decoding.readVarString(decoder)
  switch (type) {
    case MessageType.Update:
    case MessageType.Awareness:
      return { type, pageId, payload: decoding.readVarUint8Array(decoder) }
    case MessageType.Sync: {
      const count = decoding.readVarUint(decoder)
      const updates = Array.from({ length: count }, () => decoding.readVarUint8Array(decoder))
      return { type, pageId, updates }
    }
    default:
      return null
  }
}
