// Answers its start-up check and then stops reading its input: the pipe fills up and a big request blocks the writer.
import { pong, serve } from './stub.mjs'

serve((id) => {
  pong(id)
  process.stdin.pause()
})
setInterval(() => {}, 1000)
