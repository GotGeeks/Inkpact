import type { Page, WebSocketRoute } from '@playwright/test'
import * as decoding from 'lib0/decoding'
import * as encoding from 'lib0/encoding'
import { MessageType } from '../src/sync/protocol'

/**
 * The app's sync socket, passed through a route that refuses the next update to one page with `page_too_large` once it
 * is armed, the way the server does when the page reached its size limit (run B.10): nothing more of that subscription
 * is stored until the client unsubscribes. Filling a page to 12 MiB for real would take a minute of pastes. The frames
 * are read and written with lib0 and the app's own message types, as the client does.
 */

/** The type and page of a frame the client sent; null for a ping, which has no page. */
function frameOf(message: string | Buffer): { type: number; pageId: string } | null {
  if (typeof message === 'string') return null
  const decoder = decoding.createDecoder(new Uint8Array(message))
  const type = decoding.readVarUint(decoder)
  if (!decoding.hasContent(decoder)) return null
  return { type, pageId: decoding.readVarString(decoder) }
}

function errorFrame(pageId: string, code: string): Buffer {
  const encoder = encoding.createEncoder()
  encoding.writeVarUint(encoder, MessageType.Error)
  encoding.writeVarString(encoder, pageId)
  encoding.writeVarString(encoder, code)
  return Buffer.from(encoding.toUint8Array(encoder))
}

export interface RefusingSocket {
  /** The page whose next update is refused once `armed`. */
  pageId: string
  armed: boolean
  /** An update was refused, and the subscription is dropped until the client unsubscribes. */
  refused: boolean
  refusals: number
  /** Cuts the socket and refuses new ones (true), or lets the client connect again (false): the tab is offline. */
  cut(offline: boolean): void
}

export async function refusingSocket(page: Page): Promise<RefusingSocket> {
  let offline = false
  const open: WebSocketRoute[] = []
  const control: RefusingSocket = {
    pageId: '',
    armed: false,
    refused: false,
    refusals: 0,
    cut(value) {
      offline = value
      if (value) for (const route of open.splice(0)) void route.close()
    },
  }
  await page.routeWebSocket(/\/api\/sync(?:\?.*)?$/, (route) => {
    if (offline) {
      void route.close()
      return
    }
    open.push(route)
    const server = route.connectToServer()
    route.onMessage((message) => {
      const frame = frameOf(message)
      if (frame?.pageId === control.pageId) {
        if (frame.type === MessageType.Update && control.refused) return
        if (frame.type === MessageType.Update && control.armed) {
          control.armed = false
          control.refused = true
          control.refusals++
          route.send(errorFrame(frame.pageId, 'page_too_large'))
          return
        }
        if (frame.type === MessageType.Unsubscribe) control.refused = false
      }
      server.send(message)
    })
    server.onMessage((message) => route.send(message))
  })
  return control
}
