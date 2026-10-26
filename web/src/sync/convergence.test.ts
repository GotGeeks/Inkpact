import * as Y from 'yjs'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FakeSyncHub, type FakeSocket } from '../testing/fakeSyncHub'
import { SyncClient, type PageHandle } from './syncClient'

const pageId = '0a1b2c3d-0000-4000-8000-000000000001'
const CLIENTS = 3
const STEPS = 70
const SEEDS = 50

/** A small seeded generator, so a failing schedule can be run again from its number. */
function mulberry32(seed: number) {
  let a = seed
  return () => {
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/** Same CRDT document whatever the order the updates came in: state vector and delete set. */
const fingerprint = (doc: Y.Doc) => Array.from(Y.encodeSnapshot(Y.snapshot(doc)))

const ALPHABET = ['a', 'b', 'c', 'é', '😀', ' ', '\n']

interface Peer {
  client: SyncClient
  handle: PageHandle | null
  socket: FakeSocket | null
}

describe('convergence', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it.each(Array.from({ length: SEEDS }, (_, i) => i + 1))(
    'three clients with random edits, outages and mode changes end with the same document (seed %i)',
    async (seed) => {
      const random = mulberry32(seed)
      const pick = (n: number) => Math.floor(random() * n)
      const hub = new FakeSyncHub()
      // Every update a client made itself, applied to one document: what the result must contain.
      const oracle = new Y.Doc()

      const peers: Peer[] = Array.from({ length: CLIENTS }, () => {
        const peer: Peer = { client: null as never, handle: null, socket: null }
        peer.client = new SyncClient({
          url: 'ws://x',
          random,
          createSocket: () => {
            const socket = hub.createSocket() as FakeSocket
            peer.socket = socket
            return socket
          },
        })
        return peer
      })

      const open = (peer: Peer) => {
        const handle = peer.client.subscribe(pageId)
        if (handle !== peer.handle) {
          peer.handle = handle
          handle.doc.on('update', (update: Uint8Array, origin: unknown) => {
            // The client's own origin for what it gets from the server is a symbol; everything else is a local edit.
            if (typeof origin !== 'symbol') Y.applyUpdate(oracle, update)
          })
        }
      }
      for (const peer of peers) open(peer)

      const edit = (peer: Peer) => {
        // An editor that left has no document to type into any more.
        if (!peer.handle || peer.client.get(pageId) !== peer.handle) return
        const doc = peer.handle.doc
        const text = doc.getText('t')
        if (text.length > 0 && random() < 0.3) {
          const at = pick(text.length)
          text.delete(at, Math.min(1 + pick(3), text.length - at))
        } else {
          text.insert(pick(text.length + 1), ALPHABET[pick(ALPHABET.length)]!.repeat(1 + pick(3)))
        }
      }

      for (let step = 0; step < STEPS; step++) {
        const peer = peers[pick(CLIENTS)]!
        const roll = random()
        if (roll < 0.45) {
          for (let n = 1 + pick(3); n > 0; n--) edit(peer)
        } else if (roll < 0.55) {
          peer.socket?.serverClose(1006)
        } else if (roll < 0.62) {
          if (peer.socket) peer.socket.dead = true
        } else if (roll < 0.68) {
          hub.mode = hub.mode === 'diff' ? 'log' : 'diff'
        } else if (roll < 0.74) {
          hub.holding = true
        } else if (roll < 0.8) {
          hub.release()
        } else if (roll < 0.86) {
          peer.client.unsubscribe(pageId)
          if (peer.client.get(pageId) === undefined) peer.handle = null
        } else if (roll < 0.9) {
          open(peer)
        } else {
          await vi.advanceTimersByTimeAsync(pick(4000))
          continue
        }
        await vi.advanceTimersByTimeAsync(pick(300))
      }

      // Everything is repaired: the network works, nothing is held back, every page is open again.
      hub.holding = false
      // A dead connection stays dead: the client has to notice by itself, which takes the heartbeat.
      hub.release()
      for (const peer of peers) open(peer)
      // Long enough for the heartbeat to notice a dead connection (8 s) and for the reconnect and the sync after it.
      for (let i = 0; i < 40; i++) {
        await vi.advanceTimersByTimeAsync(1000)
        const settled =
          i >= 12 &&
          hub.sockets.every((socket) => !socket.dead) &&
          peers.every((p) => p.handle?.state === 'synced' && !p.handle.unsent)
        if (settled) break
      }
      await vi.advanceTimersByTimeAsync(1000)

      const expected = fingerprint(oracle)
      expect(fingerprint(hub.doc(pageId)), `seed ${seed}: the server`).toEqual(expected)
      for (const [i, peer] of peers.entries()) {
        expect(peer.handle?.state, `seed ${seed}: client ${i} synced`).toBe('synced')
        expect(fingerprint(peer.handle!.doc), `seed ${seed}: client ${i}`).toEqual(expected)
        expect(peer.handle!.doc.getText('t').toString()).toBe(oracle.getText('t').toString())
      }
      for (const peer of peers) peer.client.destroy()
    },
  )
})
