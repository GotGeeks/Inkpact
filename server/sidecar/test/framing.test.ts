import { describe, expect, it } from 'vitest'
import {
  FrameReader,
  FrameTooLargeError,
  MAX_FRAME_BYTES,
  MalformedRequestError,
  encodeRequest,
  encodeResponse,
  parseRequest,
  requestId,
} from '../src/framing.ts'

const part = (...values: number[]) => Buffer.from(values)

/** What the reader returns for a frame: the bytes after the length prefix. */
const body = (frame: Buffer) => frame.subarray(4)

describe('FrameReader', () => {
  it('returns a frame that arrives whole', () => {
    const frame = encodeRequest(7, 1, [part(1, 2, 3)])
    expect(new FrameReader().push(frame)).toEqual([body(frame)])
  })

  it('puts a frame together from single bytes', () => {
    const frame = encodeRequest(7, 2, [part(1, 2, 3), part(), part(9)])
    const reader = new FrameReader()

    const frames = [...frame].flatMap((byte) => reader.push(Uint8Array.of(byte)))

    expect(frames).toEqual([body(frame)])
  })

  it('returns two frames that arrive in one chunk, and keeps the start of a third', () => {
    const first = encodeRequest(1, 1, [part(1)])
    const second = encodeRequest(2, 2, [part(2), part(3)])
    const third = encodeRequest(3, 3, [part(4)])
    const reader = new FrameReader()

    expect(reader.push(Buffer.concat([first, second, third.subarray(0, 5)]))).toEqual([
      body(first),
      body(second),
    ])
    expect(reader.push(third.subarray(5))).toEqual([body(third)])
  })

  it('splits a frame across chunks of every size', () => {
    const frames = [encodeRequest(1, 1, [part(1, 2, 3, 4, 5)]), encodeRequest(2, 1, [])]
    const all = Buffer.concat(frames)
    for (let size = 1; size <= all.length; size++) {
      const reader = new FrameReader()
      const out: Buffer[] = []
      for (let at = 0; at < all.length; at += size)
        out.push(...reader.push(all.subarray(at, at + size)))
      expect(out, `chunks of ${size}`).toEqual(frames.map(body))
    }
  })

  it('ignores an empty chunk', () => {
    const frame = encodeRequest(1, 1, [])
    const reader = new FrameReader()
    expect(reader.push(new Uint8Array(0))).toEqual([])
    expect(reader.push(frame)).toEqual([body(frame)])
  })

  it('returns an empty frame body for a length of zero', () => {
    expect(new FrameReader().push(Uint8Array.of(0, 0, 0, 0))).toEqual([Buffer.alloc(0)])
  })

  it('refuses a frame over 256 MiB as soon as its length is known', () => {
    const header = Buffer.alloc(4)
    header.writeUInt32LE(MAX_FRAME_BYTES + 1)
    expect(() => new FrameReader().push(header)).toThrow(FrameTooLargeError)
  })

  it('waits for a frame of exactly 256 MiB without buffering it', () => {
    const header = Buffer.alloc(4)
    header.writeUInt32LE(MAX_FRAME_BYTES)
    expect(new FrameReader().push(header)).toEqual([])
  })
})

describe('parseRequest', () => {
  it('reads the id, the op and the parts, including an empty one', () => {
    const frame = encodeRequest(4_000_000_000, 3, [part(1, 2), part(), part(3)])

    const request = parseRequest(body(frame))

    expect(request).toEqual({
      id: 4_000_000_000,
      op: 3,
      parts: [part(1, 2), part(), part(3)],
    })
  })

  it('reads a request without parts', () => {
    expect(parseRequest(body(encodeRequest(1, 0, [])))).toEqual({
      id: 1,
      op: 0,
      parts: [],
    })
  })

  it('refuses what is shorter than a header', () => {
    expect(() => parseRequest(Buffer.alloc(8))).toThrow(MalformedRequestError)
  })

  it('refuses a part count the frame cannot hold', () => {
    const frame = body(encodeRequest(1, 1, [part(1)]))
    frame.writeUInt32LE(0xffffffff, 5)
    expect(() => parseRequest(frame)).toThrow(MalformedRequestError)
  })

  it('refuses a part that runs past the end of the frame', () => {
    const frame = body(encodeRequest(1, 1, [part(1, 2, 3)]))
    frame.writeUInt32LE(100, 9)
    expect(() => parseRequest(frame)).toThrow(MalformedRequestError)
  })

  it('refuses bytes after the last part', () => {
    const frame = Buffer.concat([body(encodeRequest(1, 1, [part(1)])), part(0)])
    expect(() => parseRequest(frame)).toThrow(MalformedRequestError)
  })
})

describe('encodeResponse', () => {
  it('writes the layout the server reads', () => {
    const frame = encodeResponse(5, 1, [part(7, 8)])

    expect([...frame]).toEqual([
      ...[15, 0, 0, 0], // the length of what follows
      ...[5, 0, 0, 0], // id
      1, // status
      ...[1, 0, 0, 0], // one part
      ...[2, 0, 0, 0, 7, 8],
    ])
  })
})

describe('requestId', () => {
  it('finds the id of a frame with a broken rest, and 0 when there is none', () => {
    expect(requestId(Buffer.from([9, 0, 0, 0, 1]))).toBe(9)
    expect(requestId(Buffer.from([9, 0]))).toBe(0)
  })
})
