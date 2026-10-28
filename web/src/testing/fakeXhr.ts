import { vi } from 'vitest'

interface ProgressEvent {
  lengthComputable: boolean
  loaded: number
  total: number
}

/**
 * A stand-in for XMLHttpRequest that does nothing until the test says what the server did: how much has been sent
 * (`progress`), and how it answered (`respond`). Every request that was sent is in `FakeXhr.sent`.
 */
export class FakeXhr {
  static sent: FakeXhr[] = []

  method = ''
  url = ''
  headers: Record<string, string> = {}
  body: unknown = null
  aborted = false
  status = 0
  responseText = ''
  upload: { onprogress: ((event: ProgressEvent) => void) | null } = { onprogress: null }
  onload: (() => void) | null = null
  onerror: (() => void) | null = null
  onabort: (() => void) | null = null
  private responseHeaders: Record<string, string> = {}

  open(method: string, url: string) {
    this.method = method
    this.url = url
  }

  setRequestHeader(name: string, value: string) {
    this.headers[name] = value
  }

  getResponseHeader(name: string): string | null {
    return this.responseHeaders[name] ?? null
  }

  send(body: unknown) {
    this.body = body
    FakeXhr.sent.push(this)
  }

  abort() {
    this.aborted = true
    this.onabort?.()
  }

  progress(loaded: number, total: number) {
    this.upload.onprogress?.({ lengthComputable: true, loaded, total })
  }

  respond(status: number, body?: unknown, headers: Record<string, string> = {}) {
    this.status = status
    this.responseText = body === undefined ? '' : JSON.stringify(body)
    this.responseHeaders =
      body === undefined ? headers : { 'Content-Type': 'application/json', ...headers }
    this.onload?.()
  }

  fail() {
    this.onerror?.()
  }
}

/** Replaces XMLHttpRequest for the test (undone by `vi.unstubAllGlobals()`), and forgets the requests of an earlier test. */
export function stubXhr() {
  FakeXhr.sent = []
  vi.stubGlobal('XMLHttpRequest', FakeXhr)
  return FakeXhr
}
