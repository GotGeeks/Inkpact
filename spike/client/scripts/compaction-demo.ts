// Pushes edits through a real client to the running spike server, so its compaction worker has work.
// Needs Postgres and the server (Compaction enabled, the default in appsettings.json) on :5080.
// Run with: pnpm compaction:demo [edits] [page]
import * as Y from 'yjs'
import { SyncClient } from '../src/syncClient.ts'

const edits = Number(process.argv[2] ?? 700)
const pageId = process.argv[3] ?? `compaction-demo-${Date.now().toString(36)}`

const client = new SyncClient('ws://localhost:5080/ws')
const page = client.subscribe(pageId, new Y.Doc())
while (!page.synced) await new Promise((resolve) => setTimeout(resolve, 20))

const text = page.doc.getText('t')
for (let i = 0; i < edits; i++) {
  text.insert(text.length, 'x')
  if (i % 50 === 0) await new Promise((resolve) => setTimeout(resolve, 5))
}
console.log(`${edits} edits sent to page ${pageId}`)
await new Promise((resolve) => setTimeout(resolve, 1000))
client.destroy()
