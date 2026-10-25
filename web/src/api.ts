/** An error answer from the server: a stable code the client translates, and the field it belongs to. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly field?: string,
  ) {
    super(code)
    this.name = 'ApiError'
  }
}

/**
 * Calls the JSON API. Bodies are always JSON, which is also what the server insists on for anything that
 * changes state. Errors become ApiError, so callers never have to look at a Response.
 */
export async function api<T = void>(
  method: 'GET' | 'POST',
  path: string,
  body?: unknown,
): Promise<T> {
  let response: Response
  try {
    response = await fetch(path, {
      method,
      credentials: 'same-origin',
      headers:
        method === 'GET' ? { Accept: 'application/json' } : { 'Content-Type': 'application/json' },
      body: method === 'GET' ? undefined : JSON.stringify(body ?? {}),
    })
  } catch {
    throw new ApiError(0, 'network_error')
  }

  if (response.ok) {
    return (response.status === 204 ? undefined : await response.json()) as T
  }

  let code = response.status >= 500 ? 'server_error' : 'unknown'
  let field: string | undefined
  try {
    const error = (await response.json()) as { code?: unknown; field?: unknown }
    if (typeof error.code === 'string') code = error.code
    if (typeof error.field === 'string') field = error.field
  } catch {
    // A proxy's HTML error page, for example: keep the code derived from the status.
  }

  throw new ApiError(response.status, code, field)
}
