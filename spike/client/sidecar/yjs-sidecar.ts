// Run 0.4 prototype: Yjs itself as a sidecar for the .NET server, so merging, diffing and text
// extraction run in the reference implementation the browsers use.
// Protocol: newline-delimited JSON over stdin/stdout, one request per line, binary as base64.
//   request:  { "id": 1, "op": "merge" | "diff" | "stateVector" | "text", "updates"?: [b64], "state"?: b64, "stateVector"?: b64 }
//   response: { "id": 1, "ok": true, "result": b64 | string } or { "id": 1, "ok": false, "error": "..." }
import { createInterface } from 'node:readline'
import * as Y from 'yjs'
import { plainText } from '../scripts/crdt-fixtures-text.ts'

interface Request {
  id: number
  op: 'merge' | 'replay' | 'mergeInOneTransaction' | 'diff' | 'fingerprint' | 'stateVector' | 'text'
  updates?: string[]
  state?: string
  stateVector?: string
}

const fromBase64 = (value: string) => new Uint8Array(Buffer.from(value, 'base64'))
const toBase64 = (bytes: Uint8Array) => Buffer.from(bytes).toString('base64')

/**
 * Every update in its own transaction, exactly as a browser applies them. Applying a snapshot and the
 * updates after it in ONE transaction can lose a deletion (found in run 0.5, docs/runs/0.5.md), so
 * nothing that is persisted is built that way.
 */
function load(updates: string[]): Y.Doc {
  const doc = new Y.Doc()
  for (const update of updates) Y.applyUpdate(doc, fromBase64(update))
  return doc
}

/** Kept only to pin the Yjs behaviour above in a test. */
function loadInOneTransaction(updates: string[]): Y.Doc {
  const doc = new Y.Doc()
  Y.transact(doc, () => {
    for (const update of updates) Y.applyUpdate(doc, fromBase64(update))
  })
  return doc
}

function handle(request: Request): string {
  switch (request.op) {
    case 'merge':
    case 'replay':
      // The same operation; the compactor calls 'replay' on the full history to verify a 'merge'.
      return toBase64(Y.encodeStateAsUpdate(load(request.updates ?? [])))
    case 'mergeInOneTransaction':
      return toBase64(Y.encodeStateAsUpdate(loadInOneTransaction(request.updates ?? [])))
    case 'diff':
      return toBase64(Y.encodeStateAsUpdate(load([request.state!]), fromBase64(request.stateVector!)))
    case 'fingerprint':
      // A Yjs snapshot is the state vector plus the delete set, both written in sorted order.
      return toBase64(Y.encodeSnapshot(Y.snapshot(load([request.state!]))))
    case 'stateVector':
      return toBase64(Y.encodeStateVector(load([request.state!])))
    case 'text':
      return plainText(load([request.state!]))
    default:
      throw new Error(`Unknown op ${String((request as { op: unknown }).op)}`)
  }
}

const lines = createInterface({ input: process.stdin, crlfDelay: Infinity })
lines.on('line', (line) => {
  let id = -1
  try {
    const request = JSON.parse(line) as Request
    id = request.id
    process.stdout.write(JSON.stringify({ id, ok: true, result: handle(request) }) + '\n')
  } catch (error) {
    process.stdout.write(JSON.stringify({ id, ok: false, error: String(error) }) + '\n')
  }
})
