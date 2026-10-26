// Answers every request after the start-up check with what it is allowed to do: read a file outside its folder, write
// next to itself, start a process. For the test that the sidecar runs under Node's permission model.
import { execSync } from 'node:child_process'
import { readFileSync, writeFileSync } from 'node:fs'
import { pong, respond, serve } from './stub.mjs'

const attempt = (action) => {
  try {
    action()
    return 'allowed'
  } catch (error) {
    return error.code
  }
}

serve((id, op) => {
  if (op === 0) {
    pong(id)
    return
  }
  const result = {
    readOutside: attempt(() => readFileSync(process.argv[1] + '/../../../Inkpact.Api.Tests.dll')),
    readOwn: attempt(() => readFileSync(process.argv[1])),
    write: attempt(() => writeFileSync(process.argv[1] + '.written', 'x')),
    spawn: attempt(() => execSync('echo hi')),
  }
  respond(id, [Buffer.from(JSON.stringify(result))])
})
