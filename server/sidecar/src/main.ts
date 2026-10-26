// The Yjs sidecar: the server's only decoder of Yjs updates. Reads request frames from stdin, answers
// them one at a time on stdout (see framing.ts). stdout carries the protocol, so logs go to stderr.
import {
  FrameReader,
  FrameTooLargeError,
  MalformedRequestError,
  STATUS_ERROR,
  STATUS_OK,
  encodeResponse,
  parseRequest,
  requestId,
} from './framing.ts'
import { handle } from './ops.ts'

function respond(body: Buffer): Buffer {
  let id = requestId(body)
  try {
    const request = parseRequest(body)
    id = request.id
    return encodeResponse(id, STATUS_OK, handle(request.op, request.parts))
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    if (!(error instanceof MalformedRequestError)) console.error(`request ${id} failed: ${message}`)
    return encodeResponse(id, STATUS_ERROR, [Buffer.from(message, 'utf8')])
  }
}

/** Resolves once the bytes are handed to the pipe, so a slow reader holds the next request back. */
function write(frame: Buffer): Promise<void> {
  return new Promise((resolve, reject) => {
    process.stdout.write(frame, (error) => (error ? reject(error) : resolve()))
  })
}

const reader = new FrameReader()
try {
  // One chunk at a time, and no new chunk before its frames are answered: the process holds at most one request in flight.
  for await (const chunk of process.stdin) {
    for (const body of reader.push(chunk)) {
      await write(respond(body))
    }
  }
  // stdin ended: the server is gone or is stopping, and the sidecar never outlives it.
} catch (error) {
  if (error instanceof FrameTooLargeError) {
    // The stream is broken and nothing after this point can be trusted. Say nothing more; the server restarts us.
    console.error(error.message)
    process.exit(1)
  }
  throw error
}
