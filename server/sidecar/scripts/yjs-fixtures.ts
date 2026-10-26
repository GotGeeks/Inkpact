// Writes the fixtures of the server's codec contract tests that need real Yjs and are not leftovers of the
// phase 0 spike (see server/tests/Inkpact.Api.Tests/Fixtures/yjs/README.md). Everything is deterministic.
//
//   node scripts/yjs-fixtures.ts ../tests/Inkpact.Api.Tests/Fixtures/yjs
import { readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import * as Y from 'yjs'

const directory = process.argv[2]
if (!directory) throw new Error('Usage: node scripts/yjs-fixtures.ts <fixtures directory>')

const base64 = (bytes: Uint8Array) => Buffer.from(bytes).toString('base64')
const fromBase64 = (value: string) => new Uint8Array(Buffer.from(value, 'base64'))
const fingerprint = (doc: Y.Doc) => base64(Y.encodeSnapshot(Y.snapshot(doc)))

function replay(updates: Uint8Array[], inOneTransaction: boolean): Y.Doc {
  const doc = new Y.Doc()
  if (inOneTransaction)
    Y.transact(doc, () => updates.forEach((update) => Y.applyUpdate(doc, update)))
  else updates.forEach((update) => Y.applyUpdate(doc, update))
  return doc
}

// A page that is not an editor document: no `default` fragment, only a map and a text.
{
  const doc = new Y.Doc()
  doc.clientID = 4242
  const updates: Uint8Array[] = []
  doc.on('update', (update: Uint8Array) => updates.push(update))
  doc.getMap('settings').set('title', 'not an editor page')
  doc.getText('notes').insert(0, 'hello')
  doc.getText('notes').insert(5, ' world')
  doc.getText('notes').delete(0, 1)
  writeFileSync(
    join(directory, 'non-tiptap.json'),
    JSON.stringify(
      {
        description:
          'A Y.Map "settings" and a Y.Text "notes" edited four times: no default XML fragment',
        updates: updates.map(base64),
        fingerprint: fingerprint(doc),
      },
      null,
      1,
    ) + '\n',
  )
}

// The one-transaction repro gets the fingerprint every browser has: the inputs applied one at a time. The
// same inputs in one transaction give another, which is why the sidecar never does that.
{
  const file = join(directory, 'yjs-one-transaction-repro.json')
  const repro = JSON.parse(readFileSync(file, 'utf8')) as {
    snapshot: string
    updates: string[]
    fingerprint?: string
    oneTransactionFingerprint?: string
  }
  const inputs = [repro.snapshot, ...repro.updates].map(fromBase64)
  repro.fingerprint = fingerprint(replay(inputs, false))
  repro.oneTransactionFingerprint = fingerprint(replay(inputs, true))
  if (repro.fingerprint === repro.oneTransactionFingerprint) {
    throw new Error('The repro no longer diverges: Yjs changed, see docs/spike-findings.md')
  }
  writeFileSync(file, JSON.stringify(repro, null, 1) + '\n')
}
