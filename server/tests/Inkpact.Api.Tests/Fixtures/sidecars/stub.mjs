// A stand-in for the Yjs sidecar that speaks its frames and misbehaves on purpose, for the supervision tests
// (Crdt/YjsSidecarTests). It never decodes a Yjs update. Request: [length][id][op: u8][count][parts...];
// response: [length][id][status: u8][count][count × ([length][bytes])]; all integers u32 little-endian.

export function serve(onRequest) {
  let buffered = Buffer.alloc(0)
  process.stdin.on('data', (chunk) => {
    buffered = Buffer.concat([buffered, chunk])
    while (buffered.length >= 4) {
      const length = buffered.readUInt32LE(0)
      if (buffered.length < 4 + length) break
      const body = buffered.subarray(4, 4 + length)
      buffered = buffered.subarray(4 + length)
      onRequest(body.readUInt32LE(0), body.readUInt8(4))
    }
  })
  process.stdin.on('end', () => process.exit(0))
}

export function respond(id, parts, status = 0) {
  const size = 9 + parts.reduce((sum, part) => sum + 4 + part.length, 0)
  const frame = Buffer.alloc(4 + size)
  frame.writeUInt32LE(size, 0)
  frame.writeUInt32LE(id, 4)
  frame.writeUInt8(status, 8)
  frame.writeUInt32LE(parts.length, 9)
  let position = 13
  for (const part of parts) {
    frame.writeUInt32LE(part.length, position)
    part.copy(frame, position + 4)
    position += 4 + part.length
  }
  process.stdout.write(frame)
}

export function pong(id) {
  respond(id, [Buffer.from('1'), Buffer.from('stub')])
}
