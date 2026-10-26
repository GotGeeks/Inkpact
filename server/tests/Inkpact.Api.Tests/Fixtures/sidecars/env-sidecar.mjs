// Answers every request after the start-up check with its own environment, as JSON.
import { pong, respond, serve } from './stub.mjs'

serve((id, op) => {
  if (op === 0) pong(id)
  else respond(id, [Buffer.from(JSON.stringify(process.env))])
})
