/** A socket that never connects, so a component that opens the sync socket does not reach for the network in a test. */
class IdleWebSocket {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSING = 2
  static readonly CLOSED = 3
  readyState = 0
  binaryType = 'blob'
  onopen: unknown = null
  onmessage: unknown = null
  onclose: unknown = null
  send() {}
  close() {
    this.readyState = 3
  }
}

globalThis.WebSocket = IdleWebSocket as unknown as typeof WebSocket
