// Answers its start-up check and dies on the first real request, saying why on stderr.
import { pong, serve } from './stub.mjs'

serve((id, op) => {
  if (op === 0) {
    pong(id)
    return
  }
  console.error('stub sidecar: this is the crash')
  process.exit(3)
})
