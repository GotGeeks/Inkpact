import { afterEach, describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import {
  MAX_FRAME_BYTES,
  STATUS_ERROR,
  STATUS_INVALID_CONTENT,
  encodeRequest,
} from '../src/framing.ts'
import { OP_BUILD, OP_COMPACT, OP_DIFF, OP_PING, OP_REPLACE, PROTOCOL_VERSION } from '../src/ops.ts'
import { SidecarProcess, bytes, replayed, text, typed } from './helpers.ts'

/** The sidecar as the server runs it: a child process with the protocol on stdin and stdout. */
describe('the sidecar process', () => {
  const started: SidecarProcess[] = []
  const start = () => {
    const sidecar = new SidecarProcess()
    started.push(sidecar)
    return sidecar
  }

  afterEach(async () => {
    await Promise.all(started.splice(0).map((sidecar) => sidecar.close()))
  })

  it('answers a ping', async () => {
    const answer = await start().call(OP_PING)

    expect(answer.ok).toBe(true)
    expect(text(answer.parts[0])).toBe(PROTOCOL_VERSION)
  })

  it('exits cleanly when stdin ends, so it never outlives the server', async () => {
    const sidecar = start()
    await sidecar.call(OP_PING)

    expect(await sidecar.close()).toBe(0)
  })

  it('answers a broken state vector and an undecodable update with an error, and keeps serving', async () => {
    const sidecar = start()
    const updates = typed((doc) => doc.getText('t').insert(0, 'hello'))

    const brokenVector = await sidecar.call(OP_DIFF, [bytes(255, 255, 255), ...updates])
    const brokenUpdate = await sidecar.call(OP_COMPACT, [bytes(255, 255, 255, 255)])
    const next = await sidecar.call(OP_DIFF, [bytes(), ...updates])

    expect(brokenVector.ok).toBe(false)
    expect(text(brokenVector.parts[0])).not.toBe('')
    expect(brokenUpdate.ok).toBe(false)
    expect(next.ok).toBe(true)
    expect(new Uint8Array(next.parts[0]!)).toEqual(Y.encodeStateAsUpdate(replayed(updates)))
  })

  it('answers content the editor does not allow with a status of its own, and a page it cannot read with the plain error', async () => {
    const sidecar = start()
    const content = (value: unknown) => Buffer.from(JSON.stringify(value), 'utf8')
    const iframe = content({ type: 'doc', content: [{ type: 'iframe' }] })

    const refusedBuild = await sidecar.call(OP_BUILD, [iframe])
    const refusedReplace = await sidecar.call(OP_REPLACE, [iframe])
    const brokenPage = await sidecar.call(OP_REPLACE, [
      content({ type: 'doc' }),
      bytes(255, 255, 255, 255),
    ])

    expect([refusedBuild.status, refusedReplace.status]).toEqual([
      STATUS_INVALID_CONTENT,
      STATUS_INVALID_CONTENT,
    ])
    expect(text(refusedBuild.parts[0])).toContain('iframe')
    expect(brokenPage.status).toBe(STATUS_ERROR)
  })

  it('answers a request it cannot read with an error, and keeps serving', async () => {
    const sidecar = start()

    const answer = await sidecar.callFrame((id) => {
      const frame = encodeRequest(id, OP_DIFF, [bytes(1)])
      frame.writeUInt32LE(1000, 13) // the part now claims to be longer than the frame
      return frame
    })

    expect(answer.ok).toBe(false)
    expect((await sidecar.call(OP_PING)).ok).toBe(true)
  })

  it('reads a request that arrives a byte at a time, and several in one write', async () => {
    const sidecar = start()

    const slow = await sidecar.callFrame((id) => encodeRequest(id, OP_PING, []), 1)
    const together = await Promise.all([
      sidecar.call(OP_PING),
      sidecar.call(OP_PING),
      sidecar.call(OP_PING),
    ])

    expect(slow.ok).toBe(true)
    expect(together.every((answer) => answer.ok)).toBe(true)
  })

  it('answers a 20 MB request', async () => {
    const sidecar = start()
    const doc = new Y.Doc()
    const updates: Uint8Array[] = []
    doc.on('update', (update: Uint8Array) => updates.push(update))
    doc.getText('t').insert(0, 'x'.repeat(20 * 1024 * 1024))

    const answer = await sidecar.call(OP_DIFF, [bytes(), ...updates])

    expect(answer.ok).toBe(true)
    expect(answer.parts[0]!.length).toBeGreaterThan(20 * 1024 * 1024)
  })

  it('stops with an error and writes nothing when a frame is over 256 MiB', async () => {
    const sidecar = start()
    await sidecar.call(OP_PING)
    const answered = sidecar.stdout.length

    const header = Buffer.alloc(4)
    header.writeUInt32LE(MAX_FRAME_BYTES + 1)
    sidecar.child.stdin.write(header)

    expect(await sidecar.exited).toBe(1)
    expect(sidecar.stdout.length).toBe(answered)
    expect(sidecar.stderr.join('')).toContain('over the limit')
  })
})
