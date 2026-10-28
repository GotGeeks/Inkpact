/** An error answer from the server: a stable code the client translates, and the field it belongs to. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly field?: string,
    /** How long to wait before trying again, when the server said so (a lockout or a rate limit). */
    readonly retryAfterSeconds?: number,
  ) {
    super(code)
    this.name = 'ApiError'
  }
}

const csrfHeader = 'X-CSRF-Token'

// The token for the next call that changes something. The server hands it out with the sign-in state,
// and it belongs to whoever is signed in at that moment, so every new state replaces it.
let csrfToken: string | null = null

export function setCsrfToken(token: string | null | undefined) {
  csrfToken = token ?? null
}

type Method = 'GET' | 'POST' | 'PATCH'

async function send(method: Method, path: string, body: unknown): Promise<Response> {
  // A file is sent as it is, for the one endpoint that takes one (an attachment upload); everything else is JSON.
  const file = body instanceof Blob ? body : undefined
  const headers: Record<string, string> =
    method === 'GET'
      ? { Accept: 'application/json' }
      : { 'Content-Type': file ? 'application/octet-stream' : 'application/json' }
  if (method !== 'GET' && csrfToken) headers[csrfHeader] = csrfToken

  try {
    return await fetch(path, {
      method,
      credentials: 'same-origin',
      headers,
      body: method === 'GET' ? undefined : (file ?? JSON.stringify(body ?? {})),
    })
  } catch {
    throw new ApiError(0, 'network_error')
  }
}

/** Asks the server for the sign-in state again, for the token that belongs to it. */
async function refreshCsrfToken() {
  const state = await send('GET', '/api/auth/state', undefined)
  if (state.ok) setCsrfToken(((await state.json()) as { csrfToken?: string }).csrfToken)
}

async function toError(response: Response): Promise<ApiError> {
  let code = response.status >= 500 ? 'server_error' : 'unknown'
  let field: string | undefined
  try {
    const error = (await response.clone().json()) as { code?: unknown; field?: unknown }
    if (typeof error.code === 'string') code = error.code
    if (typeof error.field === 'string') field = error.field
  } catch {
    // A proxy's HTML error page, for example: keep the code derived from the status.
  }

  const retryAfter = Number(response.headers.get('Retry-After'))
  return new ApiError(
    response.status,
    code,
    field,
    Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : undefined,
  )
}

/**
 * Calls the JSON API. Bodies are always JSON, and so is everything that changes state; those calls carry the CSRF
 * token. If the server finds the token out of date (it was issued before another tab signed in or out, say) the
 * state is fetched again and the call is repeated once. Errors become ApiError, so callers never see a Response.
 */
export async function api<T = void>(method: Method, path: string, body?: unknown): Promise<T> {
  let response = await send(method, path, body)

  if (response.status === 400 && method !== 'GET') {
    const error = await toError(response)
    if (error.code !== 'csrf_invalid') throw error

    await refreshCsrfToken()
    response = await send(method, path, body)
  }

  if (response.ok) {
    return (response.status === 204 ? undefined : await response.json()) as T
  }

  throw await toError(response)
}

/** Posts a file as the raw body, with the CSRF token and its one retry, like {@link api}. Answers with JSON. */
export function uploadFile<T>(path: string, file: Blob): Promise<T> {
  return api<T>('POST', path, file)
}

/** One upload through XMLHttpRequest, answered as a Response so that errors are read the way {@link api} reads them. */
function sendWithProgress(
  path: string,
  file: Blob,
  onProgress: (sent: number, total: number) => void,
  signal?: AbortSignal,
): Promise<Response> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('The upload was cancelled.', 'AbortError'))
      return
    }

    const request = new XMLHttpRequest()
    request.open('POST', path)
    request.setRequestHeader('Content-Type', 'application/octet-stream')
    if (csrfToken) request.setRequestHeader(csrfHeader, csrfToken)

    request.upload.onprogress = (event) => {
      if (event.lengthComputable) onProgress(event.loaded, event.total)
    }
    // Removed again once the request has ended, so that a long-lived signal does not keep it alive.
    const cancel = () => request.abort()
    const settle = () => signal?.removeEventListener('abort', cancel)

    request.onload = () => {
      settle()
      const headers = new Headers()
      const type = request.getResponseHeader('Content-Type')
      if (type) headers.set('Content-Type', type)
      const retryAfter = request.getResponseHeader('Retry-After')
      if (retryAfter) headers.set('Retry-After', retryAfter)
      // A Response refuses a body with these statuses.
      const empty = request.status === 204 || request.status === 205 || request.status === 304
      resolve(
        new Response(empty ? null : request.responseText, { status: request.status, headers }),
      )
    }
    request.onerror = () => {
      settle()
      reject(new ApiError(0, 'network_error'))
    }
    request.onabort = () => {
      settle()
      reject(new DOMException('The upload was cancelled.', 'AbortError'))
    }
    signal?.addEventListener('abort', cancel, { once: true })

    request.send(file)
  })
}

/**
 * Posts a file like {@link uploadFile}, and reports how much of it has been sent: `fetch` has no upload progress, so
 * this one goes through XMLHttpRequest. Aborting the signal ends the upload with an AbortError, not an ApiError.
 *
 * The token is asked for first. A server that refuses a large body without reading it (an out-of-date token, say) is
 * often seen by the browser as a reset connection rather than its answer, so the retry below only helps a small file.
 */
export async function uploadFileWithProgress<T>(
  path: string,
  file: Blob,
  onProgress: (sent: number, total: number) => void,
  signal?: AbortSignal,
): Promise<T> {
  // When even this fails the upload will too, and says why.
  await refreshCsrfToken().catch(() => undefined)
  let response = await sendWithProgress(path, file, onProgress, signal)

  if (response.status === 400) {
    const error = await toError(response)
    if (error.code !== 'csrf_invalid') throw error

    await refreshCsrfToken()
    response = await sendWithProgress(path, file, onProgress, signal)
  }

  if (response.ok) return (await response.json()) as T
  throw await toError(response)
}
