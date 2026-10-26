// The wire format between the server and the sidecar, over stdin and stdout. All integers are
// little-endian u32.
//
//   request:  [length][id][op: u8][count][count × ([length][bytes])]
//   response: [length][id][status: u8][count][count × ([length][bytes])]
//
// `length` at the start counts the bytes after it.

/** A frame over this is a broken stream, not a big request: nothing sane sends 256 MiB to be merged. */
export const MAX_FRAME_BYTES = 256 * 1024 * 1024

export const STATUS_OK = 0
export const STATUS_ERROR = 1

export class FrameTooLargeError extends Error {
  constructor(length: number) {
    super(`Frame of ${length} bytes is over the limit of ${MAX_FRAME_BYTES}`)
  }
}

/** A frame that arrived whole but does not say what a request says. The stream itself is fine. */
export class MalformedRequestError extends Error {}

export interface Request {
  id: number
  op: number
  parts: Buffer[]
}

/**
 * Cuts the byte stream into frames. Chunks may split a frame anywhere, or hold several frames; only
 * complete frames come out, as the bytes after the length prefix.
 */
export class FrameReader {
  #chunks: Buffer[] = []
  #buffered = 0
  #needed: number | null = null

  push(chunk: Uint8Array): Buffer[] {
    const frames: Buffer[] = []
    if (chunk.byteLength > 0) {
      this.#chunks.push(Buffer.from(chunk.buffer, chunk.byteOffset, chunk.byteLength))
      this.#buffered += chunk.byteLength
    }

    for (;;) {
      if (this.#needed === null) {
        if (this.#buffered < 4) break
        const length = this.#take(4).readUInt32LE(0)
        if (length > MAX_FRAME_BYTES) throw new FrameTooLargeError(length)
        this.#needed = length
      }
      if (this.#buffered < this.#needed) break
      frames.push(this.#take(this.#needed))
      this.#needed = null
    }
    return frames
  }

  /** Removes and returns the next `count` buffered bytes. The caller has checked they are there. */
  #take(count: number): Buffer {
    if (count === 0) return Buffer.alloc(0)
    this.#buffered -= count

    const first = this.#chunks[0]!
    if (first.length >= count) {
      // The common case, and no copy: the frame lies inside one chunk.
      if (first.length === count) this.#chunks.shift()
      else this.#chunks[0] = first.subarray(count)
      return first.subarray(0, count)
    }

    const out = Buffer.allocUnsafe(count)
    let filled = 0
    while (filled < count) {
      const chunk = this.#chunks[0]!
      const wanted = count - filled
      if (chunk.length <= wanted) {
        out.set(chunk, filled)
        filled += chunk.length
        this.#chunks.shift()
      } else {
        out.set(chunk.subarray(0, wanted), filled)
        this.#chunks[0] = chunk.subarray(wanted)
        filled = count
      }
    }
    return out
  }
}

/** The id of a frame body, for answering with an error when the rest of it makes no sense. */
export function requestId(body: Buffer): number {
  return body.length >= 4 ? body.readUInt32LE(0) : 0
}

export function parseRequest(body: Buffer): Request {
  if (body.length < 9) throw new MalformedRequestError('Request is shorter than its header')
  const id = body.readUInt32LE(0)
  const op = body.readUInt8(4)
  const count = body.readUInt32LE(5)

  // Every part costs at least its 4 length bytes, so a count that cannot fit is a lie.
  if (count > (body.length - 9) / 4) throw new MalformedRequestError('Part count does not fit')

  const parts: Buffer[] = []
  let position = 9
  for (let i = 0; i < count; i++) {
    if (position + 4 > body.length) throw new MalformedRequestError('Part is cut off')
    const length = body.readUInt32LE(position)
    position += 4
    if (position + length > body.length) throw new MalformedRequestError('Part is cut off')
    parts.push(body.subarray(position, position + length))
    position += length
  }
  if (position !== body.length) throw new MalformedRequestError('Bytes after the last part')

  return { id, op, parts }
}

// Requests and responses share one layout: only the byte after the id differs (the op, or the status).
function encodeFrame(id: number, byte: number, parts: readonly Uint8Array[]): Buffer {
  let size = 4 + 4 + 1 + 4
  for (const part of parts) size += 4 + part.length

  const out = Buffer.allocUnsafe(size)
  out.writeUInt32LE(size - 4, 0)
  out.writeUInt32LE(id, 4)
  out.writeUInt8(byte, 8)
  out.writeUInt32LE(parts.length, 9)
  let position = 13
  for (const part of parts) {
    out.writeUInt32LE(part.length, position)
    out.set(part, position + 4)
    position += 4 + part.length
  }
  return out
}

export const encodeResponse = encodeFrame

/** The server writes these; the sidecar only needs them to test itself. */
export const encodeRequest = encodeFrame
