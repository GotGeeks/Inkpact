import * as encoding from 'lib0/encoding'
import { describe, expect, it } from 'vitest'
import { decodeFrame, encodeFrame, encodePing, MessageType } from './protocol'

const pageId = '0a1b2c3d-0000-4000-8000-000000000001'

/** What `Frame.cs` and `SyncHub` write, spelled out byte by byte so a change on either side shows here. */
function idBytes(): number[] {
  return [36, ...new TextEncoder().encode(pageId)]
}

describe('frames', () => {
  it('writes a subscribe as type, page id and the state vector as one byte array', () => {
    expect([...encodeFrame(MessageType.Subscribe, pageId, new Uint8Array([0, 5]))]).toEqual([
      0,
      ...idBytes(),
      2,
      0,
      5,
    ])
  })

  it('writes an unsubscribe without a payload', () => {
    expect([...encodeFrame(MessageType.Unsubscribe, pageId)]).toEqual([1, ...idBytes()])
  })

  it('writes a ping as the single type byte', () => {
    expect([...encodePing()]).toEqual([5])
  })

  it('reads an update and an awareness frame', () => {
    expect(decodeFrame(new Uint8Array([2, ...idBytes(), 3, 1, 2, 3]))).toEqual({
      type: MessageType.Update,
      pageId,
      payload: new Uint8Array([1, 2, 3]),
    })
    expect(decodeFrame(new Uint8Array([4, ...idBytes(), 1, 9]))).toEqual({
      type: MessageType.Awareness,
      pageId,
      payload: new Uint8Array([9]),
    })
  })

  it('reads a sync in diff mode: the server state vector and one update', () => {
    expect(decodeFrame(new Uint8Array([3, ...idBytes(), 2, 1, 4, 1, 3, 7, 8, 9]))).toEqual({
      type: MessageType.Sync,
      pageId,
      stateVector: new Uint8Array([1, 4]),
      updates: [new Uint8Array([7, 8, 9])],
    })
  })

  it('reads a sync in log mode: an empty state vector and the stored updates', () => {
    expect(decodeFrame(new Uint8Array([3, ...idBytes(), 0, 2, 1, 5, 2, 6, 7]))).toEqual({
      type: MessageType.Sync,
      pageId,
      stateVector: new Uint8Array(),
      updates: [new Uint8Array([5]), new Uint8Array([6, 7])],
    })
  })

  it('reads an error with its code', () => {
    const encoder = encoding.createEncoder()
    encoding.writeVarUint(encoder, MessageType.Error)
    encoding.writeVarString(encoder, pageId)
    encoding.writeVarString(encoder, 'read_only')
    expect(decodeFrame(encoding.toUint8Array(encoder))).toEqual({
      type: MessageType.Error,
      pageId,
      code: 'read_only',
    })
  })

  it('reads a pong, and ignores a type it does not know', () => {
    expect(decodeFrame(new Uint8Array([6]))).toEqual({ type: MessageType.Pong })
    expect(decodeFrame(new Uint8Array([42, 0]))).toBeNull()
  })

  it('throws on a truncated frame', () => {
    expect(() => decodeFrame(new Uint8Array([3, ...idBytes(), 2, 1]))).toThrow()
    expect(() => decodeFrame(new Uint8Array([2, 36, 1]))).toThrow()
  })
})
