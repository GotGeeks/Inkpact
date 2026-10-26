import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import {
  OP_COMPACT,
  OP_DIFF,
  OP_FINGERPRINT,
  OP_PING,
  PROTOCOL_VERSION,
  handle,
} from '../src/ops.ts'
import { bytes, fingerprintOf, replayed, text, typed } from './helpers.ts'

/** Three people's worth of typing on one document: a few updates that depend on each other. */
function sampleUpdates(): Uint8Array[] {
  return typed(
    (doc) => doc.getText('t').insert(0, 'hello'),
    (doc) => doc.getText('t').insert(5, ' world'),
    (doc) => doc.getText('t').delete(0, 1),
    (doc) => doc.getMap('m').set('k', 'v'),
  )
}

/** The updates that a client holding the first `count` of them lacks. */
const stateVectorAfter = (updates: Uint8Array[], count: number) =>
  Y.encodeStateVector(replayed(updates.slice(0, count)))

describe('ping', () => {
  it('answers with the protocol version and the Yjs version', () => {
    const [protocol, yjs] = handle(OP_PING, [])

    expect(text(protocol)).toBe(PROTOCOL_VERSION)
    expect(text(yjs)).toMatch(/^13\.\d+\.\d+$/)
  })
})

describe('diff', () => {
  it('gives the full state for an empty state vector', () => {
    const updates = sampleUpdates()
    const full = replayed(updates)

    const [update, stateVector] = handle(OP_DIFF, [bytes(), ...updates])

    expect(update).toEqual(Y.encodeStateAsUpdate(full))
    expect(stateVector).toEqual(Y.encodeStateVector(full))
  })

  it('treats a state vector with no clients like an empty one', () => {
    const updates = sampleUpdates()

    const [update] = handle(OP_DIFF, [bytes(0), ...updates])

    expect(update).toEqual(Y.encodeStateAsUpdate(replayed(updates)))
  })

  it('gives what a partly synced client lacks: its state plus the diff is the whole page', () => {
    const updates = sampleUpdates()
    const client = replayed(updates.slice(0, 2))

    const [update, stateVector] = handle(OP_DIFF, [stateVectorAfter(updates, 2), ...updates])
    Y.applyUpdate(client, update!)

    expect(fingerprintOf(client)).toEqual(fingerprintOf(replayed(updates)))
    expect(stateVector).toEqual(Y.encodeStateVector(replayed(updates)))
    expect(update!.length).toBeLessThan(Y.encodeStateAsUpdate(replayed(updates)).length)
  })

  it('gives an update with nothing in it for a client that is up to date', () => {
    const updates = sampleUpdates()
    const [update] = handle(OP_DIFF, [stateVectorAfter(updates, updates.length), ...updates])

    const client = replayed(updates)
    const before = fingerprintOf(client)
    Y.applyUpdate(client, update!)

    expect(fingerprintOf(client)).toEqual(before)
  })

  it('answers an empty log with the empty state', () => {
    const [update, stateVector] = handle(OP_DIFF, [bytes()])

    expect(update).toEqual(Y.encodeStateAsUpdate(new Y.Doc()))
    expect(stateVector).toEqual(Y.encodeStateVector(new Y.Doc()))
  })

  it('refuses a request without the state vector', () => {
    expect(() => handle(OP_DIFF, [])).toThrow()
  })

  it('refuses a broken state vector', () => {
    expect(() => handle(OP_DIFF, [bytes(255, 255, 255), ...sampleUpdates()])).toThrow()
  })

  it('refuses an update that does not decode', () => {
    expect(() => handle(OP_DIFF, [bytes(), bytes(255, 255, 255, 255)])).toThrow()
  })
})

describe('compact', () => {
  it('gives the state, its state vector, equal fingerprints and the text', () => {
    const updates = sampleUpdates()
    const full = replayed(updates)

    const [state, stateVector, replayFingerprint, stateFingerprint, plain] = handle(
      OP_COMPACT,
      updates,
    )

    expect(state).toEqual(Y.encodeStateAsUpdate(full))
    expect(stateVector).toEqual(Y.encodeStateVector(full))
    expect(replayFingerprint).toEqual(fingerprintOf(full))
    expect(stateFingerprint).toEqual(replayFingerprint)
    expect(text(plain)).toBe('')
  })

  it('takes a snapshot first and updates that overlap it', () => {
    const updates = sampleUpdates()
    const [snapshot] = handle(OP_COMPACT, updates.slice(0, 3))

    const [state, , replayFingerprint] = handle(OP_COMPACT, [snapshot!, ...updates.slice(1)])

    expect(replayFingerprint).toEqual(fingerprintOf(replayed(updates)))
    expect(state).toEqual(Y.encodeStateAsUpdate(replayed(updates)))
  })

  it('changes nothing when it compacts its own output', () => {
    const [state, stateVector, replayFingerprint] = handle(OP_COMPACT, sampleUpdates())

    const [again, againVector, againFingerprint] = handle(OP_COMPACT, [state!])

    expect(again).toEqual(state)
    expect(againVector).toEqual(stateVector)
    expect(againFingerprint).toEqual(replayFingerprint)
  })

  it('compacts nothing into the empty document', () => {
    const [state, stateVector, replayFingerprint, stateFingerprint, plain] = handle(OP_COMPACT, [])

    expect(state).toEqual(Y.encodeStateAsUpdate(new Y.Doc()))
    expect(stateVector).toEqual(Y.encodeStateVector(new Y.Doc()))
    expect(replayFingerprint).toEqual(stateFingerprint)
    expect(text(plain)).toBe('')
  })

  it('extracts the text of an editor document', () => {
    const updates = typed((doc) => {
      const paragraph = new Y.XmlElement('paragraph')
      doc.getXmlFragment('default').insert(0, [paragraph])
      const content = new Y.XmlText()
      paragraph.insert(0, [content])
      content.insert(0, 'search me')
    })

    const parts = handle(OP_COMPACT, updates)

    expect(text(parts[4])).toBe('search me')
  })

  it('applies updates whose dependencies arrive later', () => {
    const updates = sampleUpdates()

    const [, , shuffledFingerprint] = handle(OP_COMPACT, [...updates].reverse())

    expect(shuffledFingerprint).toEqual(fingerprintOf(replayed(updates)))
  })

  it('refuses an update that does not decode', () => {
    expect(() => handle(OP_COMPACT, [...sampleUpdates(), bytes(255, 255, 255, 255)])).toThrow()
  })
})

describe('fingerprint', () => {
  it('is the fingerprint of the replay', () => {
    const updates = sampleUpdates()

    expect(handle(OP_FINGERPRINT, updates)).toEqual([fingerprintOf(replayed(updates))])
  })

  it('changes when an update is missing', () => {
    const updates = sampleUpdates()

    expect(handle(OP_FINGERPRINT, updates.slice(0, -1))).not.toEqual(
      handle(OP_FINGERPRINT, updates),
    )
  })

  it('sees a deletion that the text does not show', () => {
    const updates = typed(
      (doc) => doc.getText('t').insert(0, 'ab'),
      (doc) => doc.getText('t').delete(1, 1),
    )

    expect(handle(OP_FINGERPRINT, updates)).not.toEqual(handle(OP_FINGERPRINT, updates.slice(0, 1)))
  })
})

describe('an unknown operation', () => {
  it('is refused', () => {
    expect(() => handle(42, [])).toThrow(/Unknown operation 42/)
  })
})

// Shared with the server's contract tests, which check the same fingerprint through the real process.
const repro = JSON.parse(
  readFileSync(
    new URL(
      '../../tests/Inkpact.Api.Tests/Fixtures/yjs/yjs-one-transaction-repro.json',
      import.meta.url,
    ),
    'utf8',
  ),
) as { snapshot: string; updates: string[]; fingerprint: string; oneTransactionFingerprint: string }

describe('the one-transaction finding of run 0.5', () => {
  const inputs = [repro.snapshot, ...repro.updates].map(
    (value) => new Uint8Array(Buffer.from(value, 'base64')),
  )
  const base64 = (bytes: Uint8Array | undefined) => Buffer.from(bytes ?? []).toString('base64')

  it('keeps the deletion that every browser has, by applying one update at a time', () => {
    const [, , replayFingerprint, stateFingerprint] = handle(OP_COMPACT, inputs)

    expect(base64(replayFingerprint)).toBe(repro.fingerprint)
    expect(base64(stateFingerprint)).toBe(repro.fingerprint)
  })

  it('pins the Yjs behaviour that makes this necessary: one transaction ends up with another delete set', () => {
    const doc = new Y.Doc()
    Y.transact(doc, () => inputs.forEach((update) => Y.applyUpdate(doc, update)))

    expect(base64(fingerprintOf(doc))).toBe(repro.oneTransactionFingerprint)
    expect(repro.oneTransactionFingerprint).not.toBe(repro.fingerprint)
  })
})
