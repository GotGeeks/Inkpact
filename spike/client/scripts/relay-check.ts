// Headless check of the relay with real Yjs clients. Needs the spike server on :5080.
// Run with: pnpm check:relay
import * as Y from 'yjs'
import { connectRelay, type Status } from '../src/sync.ts'

const base = 'ws://localhost:5080/ws'
const run = Date.now().toString(36)

function client(page: string, doc = new Y.Doc()) {
  let status: Status = 'connecting'
  const handle = connectRelay(doc, `${base}?page=${page}-${run}`, (s) => (status = s))
  const text = () => doc.getText('t').toString()
  return { doc, handle, text, connected: () => status === 'connected' }
}

async function waitFor(what: string, condition: () => boolean, timeoutMs = 2000) {
  const start = Date.now()
  while (!condition()) {
    if (Date.now() - start > timeoutMs) throw new Error(`Timed out waiting for: ${what}`)
    await new Promise((resolve) => setTimeout(resolve, 20))
  }
  console.log(`ok  ${what} (${Date.now() - start} ms)`)
}

const alice = client('x')
await waitFor('alice connected', alice.connected)
alice.doc.getText('t').insert(0, 'hello')

const bob = client('x')
await waitFor('late joiner gets existing text', () => bob.text() === 'hello')

bob.doc.getText('t').insert(5, ' world')
await waitFor('edit reaches the other client', () => alice.text() === 'hello world')

const carol = client('y')
await waitFor('carol connected', carol.connected)
alice.doc.getText('t').insert(0, '> ')
await waitFor('alice edit reaches bob', () => bob.text() === '> hello world')
if (carol.text() !== '') throw new Error(`Other page leaked into carol: "${carol.text()}"`)
console.log('ok  other page sees nothing')

// Alice goes offline; both edit concurrently; Alice comes back.
alice.handle.destroy()
alice.doc.getText('t').insert(0, '[offline] ')
bob.doc.getText('t').insert(bob.text().length, '!')
const aliceAgain = client('x', alice.doc)
await waitFor('offline edit and concurrent edit merge on both sides', () =>
  aliceAgain.text() === bob.text() && bob.text() === '[offline] > hello world!',
)

aliceAgain.handle.destroy()
bob.handle.destroy()
carol.handle.destroy()
console.log('all relay checks passed')
