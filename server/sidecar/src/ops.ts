import * as Y from 'yjs'
import { plainText } from './text.ts'
import yjsPackage from 'yjs/package.json' with { type: 'json' }

/** Bumped when the frames or the operations change in a way an old server could not follow. */
export const PROTOCOL_VERSION = '1'

export const OP_PING = 0
export const OP_DIFF = 1
export const OP_COMPACT = 2
export const OP_FINGERPRINT = 3

const utf8 = (value: string) => Buffer.from(value, 'utf8')

/**
 * The state after replaying the updates in order. Every update gets its own `Y.applyUpdate`, exactly
 * as a browser applies them, and never one `Y.transact` around all of them: applying a snapshot and the
 * updates after it in one transaction can end without a deletion that every browser has (found in run
 * 0.5, docs/spike-findings.md). Nor `Y.mergeUpdates`, which cannot drop deleted content.
 */
function replay(updates: readonly Uint8Array[]): Y.Doc {
  const doc = new Y.Doc()
  try {
    for (const update of updates) Y.applyUpdate(doc, update)
  } catch (error) {
    doc.destroy()
    throw error
  }
  return doc
}

/** The state vector plus the delete set, both in sorted order: equal for equal CRDT documents, whatever they hold. */
function fingerprint(doc: Y.Doc): Uint8Array {
  return Y.encodeSnapshot(Y.snapshot(doc))
}

/** Runs `use` on the replayed document and always releases it. */
function withReplay<T>(updates: readonly Uint8Array[], use: (doc: Y.Doc) => T): T {
  const doc = replay(updates)
  try {
    return use(doc)
  } finally {
    doc.destroy()
  }
}

function ping(): Uint8Array[] {
  return [utf8(PROTOCOL_VERSION), utf8(yjsPackage.version)]
}

/** [client state vector, ...updates] -> [what the client is missing, the server's state vector] */
function diff([clientStateVector, ...updates]: Uint8Array[]): Uint8Array[] {
  if (clientStateVector === undefined) throw new Error('diff needs the client state vector')
  return withReplay(updates, (doc) => [
    // An empty state vector means "I have nothing", which Yjs spells as no state vector at all.
    Y.encodeStateAsUpdate(doc, clientStateVector.length === 0 ? undefined : clientStateVector),
    Y.encodeStateVector(doc),
  ])
}

/** The inputs (snapshot first, if any, then the updates) -> [state, its state vector, both fingerprints, text] */
function compact(inputs: Uint8Array[]): Uint8Array[] {
  return withReplay(inputs, (doc) => {
    const state = Y.encodeStateAsUpdate(doc)
    const stateVector = Y.encodeStateVector(doc)
    const replayFingerprint = fingerprint(doc)
    // The state read back on its own fresh document: catches an encoding that does not round-trip.
    const stateFingerprint = withReplay([state], fingerprint)
    return [state, stateVector, replayFingerprint, stateFingerprint, utf8(plainText(doc))]
  })
}

function fingerprintOf(updates: Uint8Array[]): Uint8Array[] {
  return [withReplay(updates, fingerprint)]
}

/** Answers one request. Anything it throws becomes an error response; the sidecar keeps serving. */
export function handle(op: number, parts: Uint8Array[]): Uint8Array[] {
  switch (op) {
    case OP_PING:
      return ping()
    case OP_DIFF:
      return diff(parts)
    case OP_COMPACT:
      return compact(parts)
    case OP_FINGERPRINT:
      return fingerprintOf(parts)
    default:
      throw new Error(`Unknown operation ${op}`)
  }
}
