import { spawn, type ChildProcessByStdio } from 'node:child_process'
import type { Readable, Writable } from 'node:stream'
import { fileURLToPath } from 'node:url'
import * as Y from 'yjs'
import { FrameReader, encodeRequest, parseRequest } from '../src/framing.ts'

/** A document that takes each step as its own update, the way a person typing makes them. */
export function typed(...steps: ((doc: Y.Doc) => void)[]): Uint8Array[] {
  const doc = new Y.Doc()
  const updates: Uint8Array[] = []
  doc.on('update', (update: Uint8Array) => updates.push(update))
  for (const step of steps) doc.transact(() => step(doc))
  return updates
}

export function replayed(updates: readonly Uint8Array[]): Y.Doc {
  const doc = new Y.Doc()
  for (const update of updates) Y.applyUpdate(doc, update)
  return doc
}

export function fingerprintOf(doc: Y.Doc): Uint8Array {
  return Y.encodeSnapshot(Y.snapshot(doc))
}

export const bytes = (...values: number[]) => new Uint8Array(values)

export const text = (part: Uint8Array | undefined) => Buffer.from(part ?? []).toString('utf8')

export interface Answer {
  ok: boolean
  parts: Buffer[]
}

/** The real sidecar process, driven the way the server drives it. */
export class SidecarProcess {
  readonly child: ChildProcessByStdio<Writable, Readable, Readable>
  readonly exited: Promise<number | null>
  readonly stdout: Buffer[] = []
  readonly stderr: string[] = []
  readonly #pending = new Map<number, (answer: Answer) => void>()
  readonly #reader = new FrameReader()
  #nextId = 1

  constructor() {
    this.child = spawn(
      process.execPath,
      [fileURLToPath(new URL('../src/main.ts', import.meta.url))],
      {
        stdio: ['pipe', 'pipe', 'pipe'],
      },
    )
    this.child.stderr.on('data', (chunk: Buffer) => this.stderr.push(chunk.toString('utf8')))
    this.child.stdout.on('data', (chunk: Buffer) => {
      this.stdout.push(chunk)
      for (const body of this.#reader.push(chunk)) {
        // A response has the layout of a request; what is called the op there is the status here.
        const { id, op: status, parts } = parseRequest(body)
        this.#pending.get(id)?.({ ok: status === 0, parts })
        this.#pending.delete(id)
      }
    })
    this.exited = new Promise((resolve) => this.child.on('exit', resolve))
  }

  call(op: number, parts: readonly Uint8Array[] = []): Promise<Answer> {
    return this.callFrame((id) => encodeRequest(id, op, parts))
  }

  /** Sends a frame chosen by the test, split into chunks of `chunkSize` bytes when given. */
  callFrame(frame: (id: number) => Buffer, chunkSize = Infinity): Promise<Answer> {
    const id = this.#nextId++
    const answer = new Promise<Answer>((resolve) => this.#pending.set(id, resolve))
    const buffer = frame(id)
    for (let at = 0; at < buffer.length; at += chunkSize) {
      this.child.stdin.write(buffer.subarray(at, at + chunkSize))
    }
    return answer
  }

  /** Ends stdin, as a server that is stopping does, and waits for the exit code. */
  close(): Promise<number | null> {
    this.child.stdin.end()
    return this.exited
  }
}
