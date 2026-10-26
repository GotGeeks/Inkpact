// Answers its start-up check and then reads everything and answers nothing: a sidecar stuck in a request.
import { pong, serve } from './stub.mjs'

serve((id, op) => {
  if (op === 0) pong(id)
})
