import type { Page } from '@playwright/test'

/** What a socket opened in the page has seen so far. */
export interface SocketLog {
  open: boolean
  closed: { code: number; reason: string } | null
  /** Each frame from the server: its type and page id, and for an update its bytes. */
  frames: { type: number; pageId: string; bytes: number[] }[]
  pongs: number
}

/**
 * Opens a sync socket inside the page, the way the app will: same origin, the page's own cookies, and the binary frames
 * of the protocol written by hand (the app's own client is tested in sync-client.spec.ts). The socket is kept on
 * window under a name, so later calls can use it.
 */
export async function openSyncSocket(page: Page, name: string): Promise<SocketLog> {
  return page.evaluate(async (socketName) => {
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:'
    const socket = new WebSocket(`${scheme}//${location.host}/api/sync`)
    socket.binaryType = 'arraybuffer'
    const log = { open: false, closed: null, frames: [], pongs: 0 } as {
      open: boolean
      closed: { code: number; reason: string } | null
      frames: { type: number; pageId: string; bytes: number[] }[]
      pongs: number
    }

    const readVarUint = (bytes: Uint8Array, at: { i: number }) => {
      let value = 0
      let shift = 0
      for (;;) {
        const b = bytes[at.i++]!
        value += (b & 0x7f) * 2 ** shift
        if (b < 0x80) return value
        shift += 7
      }
    }
    socket.addEventListener('message', (event) => {
      const bytes = new Uint8Array(event.data as ArrayBuffer)
      if (bytes.length === 1 && bytes[0] === 6) {
        log.pongs++
        return
      }
      const at = { i: 0 }
      const type = readVarUint(bytes, at)
      const idLength = readVarUint(bytes, at)
      const pageId = new TextDecoder().decode(bytes.slice(at.i, at.i + idLength))
      at.i += idLength
      let payload: number[] = []
      if (type === 2) {
        const length = readVarUint(bytes, at)
        payload = [...bytes.slice(at.i, at.i + length)]
      }
      log.frames.push({ type, pageId, bytes: payload })
    })
    socket.addEventListener('close', (event) => {
      log.closed = { code: event.code, reason: event.reason }
    })

    ;(window as unknown as Record<string, unknown>)[socketName] = { socket, log }
    await new Promise<void>((resolve) => {
      socket.addEventListener('open', () => {
        log.open = true
        resolve()
      })
      socket.addEventListener('close', () => resolve())
    })
    return structuredClone(log)
  }, name)
}

/** Sends one frame: Subscribe (0) or Update (2) for a page, with a payload, or a Ping (5) when there is no page. */
export async function sendFrame(
  page: Page,
  name: string,
  type: number,
  pageId?: string,
  payload: number[] = [],
) {
  await page.evaluate(
    ({ socketName, type, pageId, payload }) => {
      const out: number[] = []
      const writeVarUint = (value: number) => {
        while (value > 0x7f) {
          out.push(0x80 | (value & 0x7f))
          value = Math.floor(value / 128)
        }
        out.push(value)
      }
      writeVarUint(type)
      if (pageId !== undefined) {
        const id = new TextEncoder().encode(pageId)
        writeVarUint(id.length)
        out.push(...id)
        writeVarUint(payload.length)
        out.push(...payload)
      }
      const entry = (window as unknown as Record<string, { socket: WebSocket }>)[socketName]!
      entry.socket.send(new Uint8Array(out))
    },
    { socketName: name, type, pageId, payload },
  )
}

export function socketLog(page: Page, name: string): Promise<SocketLog> {
  return page.evaluate(
    (socketName) =>
      structuredClone((window as unknown as Record<string, { log: SocketLog }>)[socketName]!.log),
    name,
  )
}
