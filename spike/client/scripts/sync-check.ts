// Headless checks of the sync hub with real Yjs clients. Needs Postgres and the spike server
// (Development environment) on :5080. Run with: pnpm check:sync
import * as Y from 'yjs'
import { SyncClient, type Status } from '../src/syncClient.ts'
import { groupPresence } from '../src/presence.ts'

const server = 'localhost:5080'
const run = Date.now().toString(36)
const pageId = (name: string) => `${name}-${run}`

function connect() {
  let status: Status = 'connecting'
  const client = new SyncClient(`ws://${server}/ws`, (s) => (status = s))
  return Object.assign(client, { connected: () => status === 'connected' })
}

const text = (doc: Y.Doc) => doc.getText('t').toString()
const insert = (doc: Y.Doc, value: string) => doc.getText('t').insert(text(doc).length, value)

async function storedUpdates(page: string): Promise<number> {
  const response = await fetch(`http://${server}/debug/pages/${page}/updates`)
  return ((await response.json()) as { count: number }).count
}

async function waitFor(what: string, condition: () => boolean | Promise<boolean>, timeoutMs = 2000) {
  const start = Date.now()
  while (!(await condition())) {
    if (Date.now() - start > timeoutMs) throw new Error(`Timed out waiting for: ${what}`)
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  console.log(`ok  ${what} (${Date.now() - start} ms)`)
}

// 1. Rebuild from Postgres alone: the writer is gone before the reader arrives.
{
  const page = pageId('persist')
  const writer = connect()
  const written = writer.subscribe(page)
  await waitFor('writer synced', () => written.synced)
  insert(written.doc, 'stored in postgres')
  await waitFor('update stored', async () => (await storedUpdates(page)) === 1)
  writer.destroy()

  const reader = connect()
  const read = reader.subscribe(page)
  await waitFor('new client rebuilds the page from the database', () => text(read.doc) === 'stored in postgres')
  reader.destroy()
}

// 2. One socket, two pages; a second client only on y.
{
  const x = pageId('mux-x')
  const y = pageId('mux-y')
  const multi = connect()
  const onX = multi.subscribe(x)
  const onY = multi.subscribe(y)
  const other = connect()
  const otherY = other.subscribe(y)
  await waitFor('all pages synced', () => onX.synced && onY.synced && otherY.synced)

  insert(onX.doc, 'only x')
  insert(onY.doc, 'only y')
  await waitFor('edit on y reaches the client on y', () => text(otherY.doc) === 'only y')
  if (text(onX.doc) !== 'only x' || (await storedUpdates(x)) !== 1) throw new Error('page x got mixed up')
  console.log('ok  pages stay separate on one socket')
  multi.destroy()
  other.destroy()
}

// 3. Two tabs of one user on one page: every edit stored once, nothing echoed back.
{
  const page = pageId('tabs')
  const tabA = connect()
  const tabB = connect()
  const a = tabA.subscribe(page)
  const b = tabB.subscribe(page)
  await waitFor('both tabs synced', () => a.synced && b.synced)

  for (const [doc, value] of [[a.doc, 'a1 '], [b.doc, 'b1 '], [a.doc, 'a2 '], [b.doc, 'b2 '], [a.doc, 'a3']] as const) {
    insert(doc, value)
    await waitFor(`"${value.trim()}" reaches the other tab`, () => text(a.doc) === text(b.doc))
  }
  await waitFor('exactly 5 updates stored for 5 edits', async () => (await storedUpdates(page)) === 5)
  tabA.destroy()
  tabB.destroy()
}

// 4. Offline edits plus a concurrent online edit; reconnect sends one diff.
{
  const page = pageId('offline')
  const alice = connect()
  const bob = connect()
  const a = alice.subscribe(page)
  const b = bob.subscribe(page)
  await waitFor('both synced', () => a.synced && b.synced)
  insert(a.doc, 'base')
  await waitFor('base reaches bob', () => text(b.doc) === 'base')

  alice.destroy()
  a.doc.getText('t').insert(0, '[offline 1] ')
  a.doc.getText('t').insert(0, '[offline 2] ')
  insert(b.doc, ' +bob')
  await waitFor('bob edit stored', async () => (await storedUpdates(page)) === 2)

  const aliceAgain = connect()
  const again = aliceAgain.subscribe(page, a.doc)
  await waitFor('offline and concurrent edits merge on both sides', () =>
    text(again.doc) === text(b.doc) && text(b.doc) === '[offline 2] [offline 1] base +bob',
  )
  await waitFor('reconnect stored exactly one diff', async () => (await storedUpdates(page)) === 3)
  aliceAgain.destroy()
  bob.destroy()
}

// 5. Presence: same user in two tabs groups into one entry; a closed tab disappears quickly.
{
  const page = pageId('presence')
  const jos1 = connect()
  const jos2 = connect()
  const anna = connect()
  for (const [client, name] of [[jos1, 'jos'], [jos2, 'jos'], [anna, 'anna']] as const) {
    client.subscribe(page).awareness.setLocalStateField('user', { name, color: 'black' })
  }
  const annaView = anna.subscribe(page)
  const summary = () =>
    groupPresence(annaView.awareness.getStates()).map((g) => `${g.name}:${g.tabs}`).join(',')

  await waitFor('anna sees "anna, jos (2 tabs)"', () => summary() === 'anna:1,jos:2')
  jos2.destroy()
  await waitFor('closed tab leaves presence within 1 s', () => summary() === 'anna:1,jos:1', 1000)
  jos1.destroy()
  anna.destroy()
}

// 6. Timing: 200 sequential edits, each waited for at the other client (store + relay round trip).
{
  const page = pageId('timing')
  const writer = connect()
  const reader = connect()
  const w = writer.subscribe(page)
  const r = reader.subscribe(page)
  await waitFor('timing clients synced', () => w.synced && r.synced)

  const edits = 200
  const start = performance.now()
  for (let i = 0; i < edits; i++) {
    insert(w.doc, 'x')
    while (text(r.doc).length <= i) await new Promise((resolve) => setTimeout(resolve, 0))
  }
  const perEdit = (performance.now() - start) / edits
  console.log(`info ${edits} edits, ${perEdit.toFixed(2)} ms per edit (insert + relay, round trip)`)
  writer.destroy()
  reader.destroy()
}

console.log('all sync checks passed')
