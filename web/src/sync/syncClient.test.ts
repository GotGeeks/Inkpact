import * as Y from 'yjs'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { errorFrame, FakeSyncHub, syncFrame, textEdit } from '../testing/fakeSyncHub'
import { MessageType } from './protocol'
import { SyncClient, type ConnectionStatus } from './syncClient'

const page = '0a1b2c3d-0000-4000-8000-000000000001'
const other = '0a1b2c3d-0000-4000-8000-000000000002'

function start() {
  const hub = new FakeSyncHub()
  const events = {
    status: [] as ConnectionStatus[],
    sessionEnded: 0,
    failing: 0,
    errors: [] as string[],
    changes: 0,
    lost: [] as { pageId: string; before: Uint8Array }[],
  }
  const client = new SyncClient({
    url: 'ws://localhost/api/sync',
    createSocket: hub.createSocket,
    onStatus: (status) => events.status.push(status),
    onSessionEnded: () => events.sessionEnded++,
    onConnectFailing: () => events.failing++,
    onError: (code) => events.errors.push(code),
    onPageChange: () => events.changes++,
    onEditsLost: (pageId, before) => events.lost.push({ pageId, before }),
    // No jitter, so delays are exact.
    random: () => 0.5,
  })
  return { hub, client, events }
}

const tick = (ms = 0) => vi.advanceTimersByTimeAsync(ms)

/** The text of the page's shared field. */
const text = (doc: Y.Doc) => doc.getText('t').toString()

/** What the server holds for a page, as text. */
const serverText = (hub: FakeSyncHub, pageId = page) => text(hub.doc(pageId))

describe('SyncClient', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  describe('connecting and syncing', () => {
    it('opens a socket and reports it', async () => {
      const { events, client } = start()
      expect(client.connection).toBe('connecting')
      await tick()
      expect(client.connection).toBe('open')
      expect(events.status).toEqual(['open'])
      client.destroy()
    })

    it('subscribes with its own state vector, after running init on the new document', async () => {
      const { hub, client } = start()
      await tick()

      const handle = client.subscribe(page, { init: (doc) => textEdit(doc, 'seed') })
      const [subscribe] = hub.sent(MessageType.Subscribe, page)

      expect(subscribe!.payload).toEqual(Y.encodeStateVector(handle.doc))
      expect(text(handle.doc)).toBe('seed')
      client.destroy()
    })

    it('returns the same handle for a page that is already open', async () => {
      const { client } = start()
      await tick()
      const first = client.subscribe(page)
      const init = vi.fn()
      expect(client.subscribe(page, { init })).toBe(first)
      expect(init).not.toHaveBeenCalled()
      client.destroy()
    })

    it('gets the page in diff mode', async () => {
      const { hub, client } = start()
      hub.store(page, textEdit(new Y.Doc(), 'hello'))
      await tick()

      const handle = client.subscribe(page)
      expect(handle.state).toBe('syncing')
      await tick()

      expect(handle.state).toBe('synced')
      expect(text(handle.doc)).toBe('hello')
      client.destroy()
    })

    it('gets the page in log mode, snapshot first', async () => {
      const { hub, client } = start()
      hub.mode = 'log'
      const author = new Y.Doc()
      hub.store(page, textEdit(author, 'one '))
      // The compactor's snapshot is a full state, followed by later updates, in one list.
      const snapshot = Y.encodeStateAsUpdate(author)
      hub.log(page).splice(0, 1, snapshot)
      hub.store(page, textEdit(author, 'two'))
      await tick()

      const handle = client.subscribe(page)
      await tick()

      expect(text(handle.doc)).toBe('one two')
      client.destroy()
    })

    it('sends what the server lacks, in diff mode', async () => {
      const { hub, client } = start()
      hub.store(page, textEdit(new Y.Doc(), 'server '))
      await tick()

      const handle = client.subscribe(page, { init: (doc) => textEdit(doc, 'seed') })
      await tick()

      expect(serverText(hub)).toContain('server')
      expect(serverText(hub)).toContain('seed')
      expect(text(handle.doc)).toBe(serverText(hub))
      client.destroy()
    })

    it('sends what the server lacks, in log mode, and only that', async () => {
      const { hub, client } = start()
      hub.mode = 'log'
      const shared = new Y.Doc()
      hub.store(page, textEdit(shared, 'shared '))
      await tick()

      // A client that has the first update already and one of its own: the server lacks only the second.
      const handle = client.subscribe(page, {
        init: (doc) => {
          Y.applyUpdate(doc, hub.log(page)[0]!)
          textEdit(doc, 'mine')
        },
      })
      await tick()

      const updates = hub.sent(MessageType.Update, page)
      expect(updates).toHaveLength(1)
      expect(text(handle.doc)).toBe(serverText(hub))
      expect(serverText(hub)).toContain('mine')
      client.destroy()
    })

    it('holds local edits until the sync, and sends them once', async () => {
      const { hub, client } = start()
      hub.store(page, textEdit(new Y.Doc(), 'base '))
      await tick()

      const handle = client.subscribe(page)
      textEdit(handle.doc, 'typed-early ')
      expect(hub.sent(MessageType.Update)).toHaveLength(0)
      expect(handle.unsent).toBe(true)
      await tick()

      expect(hub.sent(MessageType.Update)).toHaveLength(1)
      expect(hub.log(page)).toHaveLength(2)
      expect(handle.unsent).toBe(false)
      client.destroy()
    })

    it('sends edits made after the sync at once, and does not echo what it receives', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      await tick()

      textEdit(handle.doc, 'a')
      expect(hub.sent(MessageType.Update)).toHaveLength(1)

      // Another client's update arrives: applied, never sent back.
      const remote = new Y.Doc()
      Y.applyUpdate(remote, Y.encodeStateAsUpdate(handle.doc))
      const update = textEdit(remote, 'b')
      hub.store(page, update)
      hub.current.deliver(wire(MessageType.Update, page, update))
      expect(text(handle.doc)).toBe('ab')
      expect(hub.sent(MessageType.Update)).toHaveLength(1)
      client.destroy()
    })

    it('ignores frames for a page it does not have', async () => {
      const { hub, client } = start()
      await tick()
      expect(() =>
        hub.current.deliver(wire(MessageType.Update, other, new Uint8Array([0, 0]))),
      ).not.toThrow()
      client.destroy()
    })

    it('starts over when a message cannot be read', async () => {
      const { hub, client } = start()
      const error = vi.spyOn(console, 'error').mockImplementation(() => {})
      await tick()
      hub.current.deliver(new Uint8Array([3, 36]))
      expect(client.connection).toBe('offline')
      await tick(250)
      expect(client.connection).toBe('open')
      expect(error).toHaveBeenCalled()
      client.destroy()
    })
  })

  describe('reconnecting', () => {
    it.each([1001, 1006, 1008, 1009, 1011])(
      'reconnects and resyncs after close code %i',
      async (code) => {
        vi.spyOn(console, 'error').mockImplementation(() => {})
        const { hub, client } = start()
        await tick()
        const handle = client.subscribe(page)
        await tick()
        expect(handle.state).toBe('synced')

        hub.current.serverClose(code, 'x')
        expect(client.connection).toBe('offline')
        expect(handle.state).toBe('syncing')
        await tick(250)

        expect(client.connection).toBe('open')
        expect(hub.sent(MessageType.Subscribe, page)).toHaveLength(2)
        await tick()
        expect(handle.state).toBe('synced')
        client.destroy()
      },
    )

    it('backs off 250 ms, doubling to 5 s', async () => {
      const { hub, client } = start()
      await tick()
      hub.refusing = true
      hub.current.serverClose(1006)

      const gaps: number[] = []
      let last = Date.now()
      let seen = hub.attempts
      while (gaps.length < 7) {
        await tick(10)
        if (hub.attempts !== seen) {
          seen = hub.attempts
          gaps.push(Date.now() - last)
          last = Date.now()
        }
      }
      expect(gaps).toEqual([250, 500, 1000, 2000, 4000, 5000, 5000])
      client.destroy()
    })

    it('adds jitter of at most 20 percent', async () => {
      const hub = new FakeSyncHub()
      const delays: number[] = []
      for (const random of [0, 1]) {
        const client = new SyncClient({
          url: 'ws://x',
          createSocket: hub.createSocket,
          random: () => random,
        })
        await tick()
        const before = hub.attempts
        const t0 = Date.now()
        hub.current.serverClose(1006)
        let waited = 0
        while (hub.attempts === before && waited < 1000) {
          await tick(1)
          waited++
        }
        delays.push(Date.now() - t0)
        client.destroy()
      }
      expect(delays[0]).toBe(200)
      expect(delays[1]).toBe(300)
    })

    it('does not forget the backoff until a sync was answered', async () => {
      vi.spyOn(console, 'error').mockImplementation(() => {})
      const { hub, client } = start()
      await tick()
      client.subscribe(page)
      hub.holding = true // no Sync ever arrives
      const gaps: number[] = []
      let last = Date.now()
      for (let round = 0; round < 3; round++) {
        hub.current.serverClose(1011)
        const before = hub.attempts
        while (hub.attempts === before) await tick(10)
        await tick() // the socket opens, the subscribe goes out
        gaps.push(Date.now() - last)
        last = Date.now()
      }
      expect(gaps[1]! - gaps[0]!).toBeGreaterThanOrEqual(0)
      // 250, then 500, then 1000: opening alone never reset it.
      expect(gaps[2]!).toBeGreaterThanOrEqual(1000)
      client.destroy()
    })

    it('goes to the sign-in check after three attempts that never opened, and keeps trying', async () => {
      const { hub, client, events } = start()
      await tick()
      hub.refusing = true
      hub.current.serverClose(1006)
      await tick(750)
      expect(events.failing).toBe(0)
      await tick(1000)
      expect(events.failing).toBe(1)
      await tick(60_000)
      expect(hub.attempts).toBeGreaterThan(4)
      client.destroy()
    })

    it('does not reconnect when the session ended (4401)', async () => {
      const { hub, client, events } = start()
      await tick()
      client.subscribe(page)
      await tick()

      hub.current.serverClose(4401, 'session_ended')
      await tick(60_000)

      expect(events.sessionEnded).toBe(1)
      expect(client.connection).toBe('offline')
      expect(hub.attempts).toBe(1)
      client.destroy()
    })

    it('resubscribes every page with its current state vector', async () => {
      const { hub, client } = start()
      await tick()
      const a = client.subscribe(page)
      const b = client.subscribe(other)
      await tick()
      textEdit(a.doc, 'x')

      hub.current.serverClose(1006)
      await tick(250)

      const subscribes = hub.received.filter(
        (entry) => entry.socket === 1 && entry.frame.type === MessageType.Subscribe,
      )
      expect(subscribes.map((entry) => entry.frame.pageId).sort()).toEqual([page, other].sort())
      expect(subscribes.find((entry) => entry.frame.pageId === page)!.frame.payload).toEqual(
        Y.encodeStateVector(a.doc),
      )
      expect(b.state).toBe('synced')
      client.destroy()
    })

    it('merges edits made while disconnected, from both sides', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      await tick()
      textEdit(handle.doc, 'base ')

      hub.current.serverClose(1006)
      textEdit(handle.doc, 'offline ')
      hub.store(page, textEdit(hub.doc(page), 'meanwhile '))
      expect(handle.unsent).toBe(true)
      await tick(250)
      await tick()

      expect(text(handle.doc)).toBe(serverText(hub))
      expect(serverText(hub)).toContain('offline')
      expect(serverText(hub)).toContain('meanwhile')
      expect(handle.unsent).toBe(false)
      client.destroy()
    })
  })

  describe('offline edits that a change from elsewhere deleted', () => {
    /** The page as the editor keeps it: paragraphs of text in the `default` fragment. */
    function paragraphs(doc: Y.Doc): string[] {
      return doc
        .getXmlFragment('default')
        .toArray()
        .map((node) => ((node as Y.XmlElement).get(0) as Y.XmlText).toString())
    }

    /** An update from `doc` (which has the page) that makes a change, as another client or the import would. */
    function editOf(doc: Y.Doc, change: (fragment: Y.XmlFragment) => void): Uint8Array {
      const before = Y.encodeStateVector(doc)
      doc.transact(() => change(doc.getXmlFragment('default')))
      return Y.encodeStateAsUpdate(doc, before)
    }

    function paragraph(text: string): Y.XmlElement {
      const element = new Y.XmlElement('paragraph')
      const content = new Y.XmlText()
      content.insert(0, text)
      element.insert(0, [content])
      return element
    }

    /** A page with one paragraph, open in the client, then the connection lost. */
    async function offlineOnAPage(mode: 'diff' | 'log' = 'diff') {
      const started = start()
      started.hub.mode = mode
      const author = new Y.Doc()
      started.hub.store(
        page,
        editOf(author, (fragment) => fragment.insert(0, [paragraph('old')])),
      )
      await tick()
      const handle = started.client.subscribe(page)
      await tick()
      started.hub.current.serverClose(1006)
      return { ...started, author, handle }
    }

    const typeInto = (doc: Y.Doc, text: string) =>
      doc.transact(() =>
        ((doc.getXmlFragment('default').get(0) as Y.XmlElement).get(0) as Y.XmlText).insert(
          3,
          text,
        ),
      )

    it.each(['diff', 'log'] as const)(
      'reports text typed offline into a block that was deleted meanwhile, with the page as it was (%s mode)',
      async (mode) => {
        const { hub, client, events, author, handle } = await offlineOnAPage(mode)
        typeInto(handle.doc, ' typed offline')
        // An import replaced the page meanwhile: every old block deleted, the new content written.
        hub.store(
          page,
          editOf(author, (fragment) => {
            fragment.delete(0, fragment.length)
            fragment.insert(0, [paragraph('new')])
          }),
        )

        await tick(250)
        await tick()

        expect(paragraphs(handle.doc)).toEqual(['new'])
        expect(events.lost.map((lost) => lost.pageId)).toEqual([page])
        const before = new Y.Doc()
        Y.applyUpdate(before, events.lost[0]!.before)
        expect(paragraphs(before)).toEqual(['old typed offline'])
        client.destroy()
      },
    )

    it('reports nothing when what was typed offline survives the change from elsewhere', async () => {
      const { hub, client, events, author, handle } = await offlineOnAPage()
      handle.doc.transact(() => handle.doc.getXmlFragment('default').insert(1, [paragraph('mine')]))
      hub.store(
        page,
        editOf(author, (fragment) => fragment.insert(0, [paragraph('theirs')])),
      )

      await tick(250)
      await tick()

      expect(paragraphs(handle.doc).sort()).toEqual(['mine', 'old', 'theirs'])
      expect(events.lost).toEqual([])
      client.destroy()
    })

    it('reports nothing when a block was deleted elsewhere and nothing was typed here', async () => {
      const { hub, client, events, author, handle } = await offlineOnAPage()
      hub.store(
        page,
        editOf(author, (fragment) => fragment.delete(0, 1)),
      )

      await tick(250)
      await tick()

      expect(paragraphs(handle.doc)).toEqual([])
      expect(events.lost).toEqual([])
      client.destroy()
    })

    it('reports nothing when the person deleted their own offline text', async () => {
      const { hub, client, events, handle } = await offlineOnAPage()
      typeInto(handle.doc, ' oops')
      handle.doc.transact(() => handle.doc.getXmlFragment('default').delete(0, 1))
      hub.store(page, textEdit(hub.doc(page), 'unrelated'))

      await tick(250)
      await tick()

      expect(events.lost).toEqual([])
      client.destroy()
    })
  })

  describe('heartbeat and browser events', () => {
    it('pings every 3 seconds', async () => {
      const { hub, client } = start()
      await tick()
      await tick(9000)
      expect(hub.pings).toBe(3)
      expect(client.connection).toBe('open')
      client.destroy()
    })

    it('stays connected for as long as the server answers', async () => {
      const { hub, client } = start()
      await tick()
      await tick(120_000)
      expect(client.connection).toBe('open')
      expect(hub.attempts).toBe(1)
      client.destroy()
    })

    it('drops a connection that has been silent for 8 seconds, and reconnects', async () => {
      const { hub, client } = start()
      await tick()
      hub.blackholed = true
      await tick(6000)
      expect(client.connection).toBe('open')
      await tick(3000)
      expect(client.connection).toBe('offline')

      hub.blackholed = false
      await tick(250)
      expect(client.connection).toBe('open')
      expect(hub.attempts).toBe(2)
      client.destroy()
    })

    it('counts any message as life, not only a pong', async () => {
      const { hub, client } = start()
      hub.answerPings = false
      await tick()
      client.subscribe(page)
      for (let i = 0; i < 10; i++) {
        await tick(3000)
        hub.current.deliver(errorFrame(other, 'too_many_pages')) // any frame
      }
      expect(client.connection).toBe('open')
      client.destroy()
    })

    it('pings instead of dropping when its own timer was throttled', async () => {
      const { hub, client } = start()
      await tick()
      // A hidden tab: a minute passes and no timer fires.
      vi.setSystemTime(Date.now() + 60_000)
      await tick(3000)

      expect(client.connection).toBe('open')
      expect(hub.pings).toBeGreaterThanOrEqual(1)
      client.destroy()
    })

    it('pings at once when the tab becomes visible', async () => {
      const { hub, client } = start()
      await tick()
      const before = hub.pings
      document.dispatchEvent(new Event('visibilitychange'))
      expect(hub.pings).toBe(before + 1)
      client.destroy()
    })

    it('drops at once on the offline event, and reconnects at once on the online event', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      await tick()

      hub.accepting = false
      window.dispatchEvent(new Event('offline'))
      expect(client.connection).toBe('offline')
      textEdit(handle.doc, 'while offline')

      hub.accepting = true
      window.dispatchEvent(new Event('online'))
      await tick()
      expect(client.connection).toBe('open')
      await tick()
      expect(serverText(hub)).toContain('while offline')
      client.destroy()
    })

    it('ignores a late close from a socket it gave up on', async () => {
      const { hub, client } = start()
      await tick()
      const old = hub.current
      const lateClose = old.onclose!

      window.dispatchEvent(new Event('offline'))
      window.dispatchEvent(new Event('online'))
      await tick()
      expect(client.connection).toBe('open')

      lateClose({ code: 1006, reason: '' })
      expect(client.connection).toBe('open')
      expect(hub.attempts).toBe(2)
      client.destroy()
    })
  })

  describe('errors from the server', () => {
    it('marks a page the server does not have as missing, and sends nothing for it', async () => {
      const { hub, client } = start()
      hub.missing.add(page)
      await tick()
      const handle = client.subscribe(page)
      await tick()

      expect(handle.state).toBe('missing')
      textEdit(handle.doc, 'x')
      handle.awareness.setLocalState({ name: 'me' })
      expect(hub.sent(MessageType.Update)).toHaveLength(0)
      expect(hub.sent(MessageType.Awareness)).toHaveLength(0)
      expect(client.connection).toBe('open')
      client.destroy()
    })

    it('stops sending after read_only', async () => {
      const { hub, client } = start()
      hub.readOnly.add(page)
      await tick()
      const handle = client.subscribe(page)
      await tick()

      textEdit(handle.doc, 'a')
      await tick()
      expect(handle.readOnly).toBe(true)
      textEdit(handle.doc, 'b')

      expect(hub.sent(MessageType.Update)).toHaveLength(1)
      client.destroy()
    })

    it('reports an error that belongs to the connection', async () => {
      const { hub, client, events } = start()
      await tick()
      client.subscribe(page)
      await tick()
      hub.current.deliver(errorFrame(page, 'too_many_pages'))
      expect(events.errors).toEqual(['too_many_pages'])
      client.destroy()
    })

    it('never sends an update for a read-only page, not even the diff after the sync', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page, {
        readOnly: true,
        init: (doc) => textEdit(doc, 'seed'),
      })
      await tick()
      textEdit(handle.doc, 'x')

      expect(handle.state).toBe('synced')
      expect(hub.sent(MessageType.Update)).toHaveLength(0)
      expect(client.hasUnsent()).toBe(false)
      client.destroy()
    })
  })

  describe('size limit', () => {
    it('does not send a change over the limit, and does not loop', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      await tick()

      textEdit(handle.doc, 'x'.repeat(2.2 * 1024 * 1024))
      expect(handle.state).toBe('tooLarge')
      textEdit(handle.doc, 'more')
      await tick(60_000)

      expect(hub.sent(MessageType.Update)).toHaveLength(0)
      expect(hub.attempts).toBe(1)
      client.destroy()
    })

    it('does not send a too large diff after a reconnect either', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      await tick()
      hub.current.serverClose(1006)
      textEdit(handle.doc, 'x'.repeat(2.2 * 1024 * 1024))
      await tick(250)
      await tick()

      expect(handle.state).toBe('tooLarge')
      expect(hub.sent(MessageType.Update)).toHaveLength(0)
      client.destroy()
    })
  })

  describe('leaving a page', () => {
    it('unsubscribes at once when everything is sent', async () => {
      const { hub, client } = start()
      await tick()
      client.subscribe(page)
      await tick()

      client.unsubscribe(page)

      expect(hub.sent(MessageType.Unsubscribe, page)).toHaveLength(1)
      expect(client.get(page)).toBeUndefined()
      client.destroy()
    })

    it('keeps a page with unsent changes until the next sync has sent them', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      await tick()
      hub.current.serverClose(1006)
      textEdit(handle.doc, 'unsent')

      client.unsubscribe(page)
      expect(client.get(page)).toBe(handle)
      expect(client.hasUnsent()).toBe(true)

      await tick(250)
      await tick()
      expect(serverText(hub)).toContain('unsent')
      expect(hub.sent(MessageType.Unsubscribe, page)).toHaveLength(1)
      expect(client.get(page)).toBeUndefined()
      expect(client.hasUnsent()).toBe(false)
      client.destroy()
    })

    it('gives the same document back when the page is opened again while it is kept', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      await tick()
      hub.current.serverClose(1006)
      textEdit(handle.doc, 'unsent')
      client.unsubscribe(page)

      const again = client.subscribe(page)
      expect(again).toBe(handle)
      await tick(250)
      await tick()
      expect(client.get(page)).toBe(handle)
      expect(hub.sent(MessageType.Unsubscribe)).toHaveLength(0)
      client.destroy()
    })
  })

  describe('awareness', () => {
    it('sends local presence once synced, and does not send what it receives', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      handle.awareness.setLocalState({ name: 'a' })
      expect(hub.sent(MessageType.Awareness)).toHaveLength(0) // not synced yet
      await tick()
      // The sync announced it.
      expect(hub.sent(MessageType.Awareness)).toHaveLength(1)

      const peer = new Y.Doc()
      const peerAwareness = new (
        handle.awareness.constructor as new (doc: Y.Doc) => typeof handle.awareness
      )(peer)
      peerAwareness.setLocalState({ name: 'b' })
      const { encodeAwarenessUpdate } = await import('y-protocols/awareness')
      hub.current.deliver(
        wire(MessageType.Awareness, page, encodeAwarenessUpdate(peerAwareness, [peer.clientID])),
      )
      expect(handle.awareness.getStates().size).toBe(2)
      expect(hub.sent(MessageType.Awareness)).toHaveLength(1)
      client.destroy()
    })

    it('announces itself again after every sync', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      handle.awareness.setLocalState({ name: 'a' })
      await tick()
      hub.current.serverClose(1006)
      await tick(250)
      await tick()

      expect(hub.sent(MessageType.Awareness).length).toBeGreaterThanOrEqual(2)
      client.destroy()
    })

    it('removes its state before it unsubscribes', async () => {
      const { hub, client } = start()
      await tick()
      const handle = client.subscribe(page)
      handle.awareness.setLocalState({ name: 'a' })
      await tick()
      const before = hub.sent(MessageType.Awareness).length

      client.unsubscribe(page)

      const types = hub.received.map((entry) => entry.frame.type)
      expect(hub.sent(MessageType.Awareness).length).toBe(before + 1)
      expect(types.at(-1)).toBe(MessageType.Unsubscribe)
      expect(types.at(-2)).toBe(MessageType.Awareness)
      client.destroy()
    })
  })

  it('a sync frame for a state it already has changes nothing', async () => {
    const { hub, client } = start()
    await tick()
    const handle = client.subscribe(page, { init: (doc) => textEdit(doc, 'a') })
    await tick()
    const doc = handle.doc
    hub.current.deliver(syncFrame(page, Y.encodeStateVector(doc), [Y.encodeStateAsUpdate(doc)]))
    expect(text(doc)).toBe('a')
    client.destroy()
  })
})

/** A frame from the server, as bytes. */
function wire(type: number, pageId: string, payload: Uint8Array): Uint8Array {
  return syncLike(type, pageId, payload)
}

function syncLike(type: number, pageId: string, payload: Uint8Array): Uint8Array {
  const id = new TextEncoder().encode(pageId)
  return new Uint8Array([type, id.length, ...id, ...varUint(payload.length), ...payload])
}

function varUint(value: number): number[] {
  const out: number[] = []
  while (value > 0x7f) {
    out.push(0x80 | (value & 0x7f))
    value = Math.floor(value / 128)
  }
  out.push(value)
  return out
}
