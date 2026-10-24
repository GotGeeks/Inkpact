// Round trip for run 0.4: feeds what yrs produced (spike/server/Spike.Crdt.Tests/crdt-out) into real Yjs.
// Run `dotnet test spike/server/Spike.Crdt.Tests` first, then: pnpm crdt:verify
import { existsSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import * as Y from 'yjs'
import { plainText } from './crdt-fixtures-text.ts'

const fixtures = fileURLToPath(new URL('../../server/Spike.Crdt.Tests/Fixtures/', import.meta.url))
const out = fileURLToPath(new URL('../../server/Spike.Crdt.Tests/crdt-out/', import.meta.url))
const bytes = (base64: string) => new Uint8Array(Buffer.from(base64, 'base64'))

let failures = 0
for (const name of ['tiptap-small', 'tiptap-10k', 'no-format-2k', 'insert-only-2k']) {
  const fixture = JSON.parse(readFileSync(`${fixtures}${name}.json`, 'utf8'))
  if (!existsSync(`${out}yrs-${name}.merged.bin`)) throw new Error(`Missing yrs output for ${name}; run the .NET tests first`)

  const fromSnapshot = new Y.Doc()
  Y.applyUpdate(fromSnapshot, readFileSync(`${out}yrs-${name}.merged.bin`))
  const snapshotOk = plainText(fromSnapshot) === fixture.expectedText

  const partial = new Y.Doc()
  for (const update of fixture.updates.slice(0, fixture.partialCount)) Y.applyUpdate(partial, bytes(update))
  Y.applyUpdate(partial, readFileSync(`${out}yrs-${name}.diff.bin`))
  const diffOk = plainText(partial) === fixture.expectedText

  // The production path: a browser loads the server's (yrs) snapshot, then gets the newer updates.
  const browser = new Y.Doc()
  Y.applyUpdate(browser, readFileSync(`${out}yrs-${name}.partial-snapshot.bin`))
  for (const update of fixture.updates.slice(fixture.partialCount)) Y.applyUpdate(browser, bytes(update))
  const incrementalOk = plainText(browser) === fixture.expectedText

  const ok = snapshotOk && diffOk && incrementalOk
  const verdict = (pass: boolean) => (pass ? 'matches' : 'DIFFERS')
  console.log(
    `${ok ? 'ok  ' : 'FAIL'} ${name}: yrs snapshot ${verdict(snapshotOk)}, yrs diff ${verdict(diffOk)}, ` +
      `yrs half-snapshot + newer updates ${verdict(incrementalOk)} (all applied in Yjs)`,
  )
  if (!ok) failures++
}
if (failures > 0) throw new Error(`${failures} fixture(s) failed the Yjs round trip`)
