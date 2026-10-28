import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api, setCsrfToken, uploadFile, uploadFileWithProgress } from './api'
import { FakeXhr, stubXhr } from './testing/fakeXhr'
import { jsonResponse, stubApi } from './testing/helpers'

beforeEach(() => setCsrfToken(null))
afterEach(() => vi.unstubAllGlobals())

describe('api', () => {
  it('returns the json body of a successful call', async () => {
    stubApi([{ method: 'GET', path: '/api/x', respond: () => jsonResponse(200, { a: 1 }) }])

    expect(await api('GET', '/api/x')).toEqual({ a: 1 })
  })

  it('returns nothing for 204', async () => {
    stubApi([{ method: 'POST', path: '/api/x', respond: () => jsonResponse(204) }])

    expect(await api('POST', '/api/x')).toBeUndefined()
  })

  it('sends json, with the cookie, and always has a body for a post', async () => {
    const fetchMock = stubApi([
      { method: 'POST', path: '/api/x', respond: () => jsonResponse(204) },
    ])

    await api('POST', '/api/x', { email: 'a@b.c' })
    await api('POST', '/api/x')

    const [, first] = fetchMock.mock.calls[0]!
    expect(first.headers).toEqual({ 'Content-Type': 'application/json' })
    expect(first.credentials).toBe('same-origin')
    expect(JSON.parse(first.body)).toEqual({ email: 'a@b.c' })
    expect(fetchMock.mock.calls[1]![1].body).toBe('{}')
  })

  it('does not send a body with a get', async () => {
    const fetchMock = stubApi([
      { method: 'GET', path: '/api/x', respond: () => jsonResponse(200, {}) },
    ])

    await api('GET', '/api/x')

    expect(fetchMock.mock.calls[0]![1].body).toBeUndefined()
  })

  it('turns an error answer into an ApiError with the code and field', async () => {
    stubApi([
      {
        method: 'POST',
        path: '/api/x',
        respond: () => jsonResponse(400, { code: 'password_too_short', field: 'password' }),
      },
    ])

    const error = await api('POST', '/api/x').catch((e) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status: 400, code: 'password_too_short', field: 'password' })
  })

  it('keeps a useful code when the error is not json', async () => {
    stubApi([
      {
        method: 'GET',
        path: '/a',
        respond: () => new Response('<html>Bad gateway</html>', { status: 502 }),
      },
      { method: 'GET', path: '/b', respond: () => new Response('nope', { status: 418 }) },
    ])

    expect(await api('GET', '/a').catch((e) => e)).toMatchObject({
      status: 502,
      code: 'server_error',
    })
    expect(await api('GET', '/b').catch((e) => e)).toMatchObject({ status: 418, code: 'unknown' })
  })

  it('reports a network failure as its own code', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    expect(await api('GET', '/api/x').catch((e) => e)).toMatchObject({
      status: 0,
      code: 'network_error',
    })
  })
})

describe('api and the CSRF token', () => {
  function sequence(...answers: Response[]) {
    const fetchMock = vi.fn()
    for (const answer of answers) fetchMock.mockResolvedValueOnce(answer)
    vi.stubGlobal('fetch', fetchMock)
    return fetchMock
  }

  const refused = () => jsonResponse(400, { code: 'csrf_invalid' })

  it('sends the token with every call that changes something', async () => {
    const fetchMock = sequence(jsonResponse(204))
    setCsrfToken('token-1')

    await api('POST', '/api/x', {})

    expect(fetchMock.mock.calls[0]![1].headers).toEqual({
      'Content-Type': 'application/json',
      'X-CSRF-Token': 'token-1',
    })
  })

  it('does not send the token with a read', async () => {
    const fetchMock = sequence(jsonResponse(200, {}))
    setCsrfToken('token-1')

    await api('GET', '/api/x')

    expect(fetchMock.mock.calls[0]![1].headers).toEqual({ Accept: 'application/json' })
  })

  it('sends nothing extra when there is no token yet', async () => {
    const fetchMock = sequence(jsonResponse(204))

    await api('POST', '/api/x', {})

    expect(fetchMock.mock.calls[0]![1].headers).toEqual({ 'Content-Type': 'application/json' })
  })

  it('fetches a new token and repeats the call once when the token is out of date', async () => {
    const fetchMock = sequence(
      refused(),
      jsonResponse(200, { csrfToken: 'token-2' }),
      jsonResponse(200, { done: true }),
    )
    setCsrfToken('token-1')

    expect(await api('POST', '/api/x', { a: 1 })).toEqual({ done: true })

    expect(fetchMock.mock.calls.map(([path, init]) => [init.method, path])).toEqual([
      ['POST', '/api/x'],
      ['GET', '/api/auth/state'],
      ['POST', '/api/x'],
    ])
    expect(fetchMock.mock.calls[2]![1].headers['X-CSRF-Token']).toBe('token-2')
    expect(JSON.parse(fetchMock.mock.calls[2]![1].body)).toEqual({ a: 1 })
  })

  it('gives up after one retry', async () => {
    const fetchMock = sequence(refused(), jsonResponse(200, { csrfToken: 'token-2' }), refused())
    setCsrfToken('token-1')

    const error = await api('POST', '/api/x', {}).catch((e) => e)

    expect(error).toMatchObject({ status: 400, code: 'csrf_invalid' })
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('does not retry any other 400', async () => {
    const fetchMock = sequence(jsonResponse(400, { code: 'email_invalid', field: 'email' }))
    setCsrfToken('token-1')

    const error = await api('POST', '/api/x', {}).catch((e) => e)

    expect(error).toMatchObject({ code: 'email_invalid', field: 'email' })
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('does not retry a read', async () => {
    const fetchMock = sequence(refused())

    await api('GET', '/api/x').catch(() => {})

    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('still reports the error when the state cannot be fetched for the retry', async () => {
    sequence(refused(), jsonResponse(500, { code: 'server_error' }), refused())

    expect(await api('POST', '/api/x', {}).catch((e) => e)).toMatchObject({ code: 'csrf_invalid' })
  })
})

describe('uploadFile', () => {
  function sequence(...answers: Response[]) {
    const fetchMock = vi.fn()
    for (const answer of answers) fetchMock.mockResolvedValueOnce(answer)
    vi.stubGlobal('fetch', fetchMock)
    return fetchMock
  }

  it('sends the file itself as raw bytes, with the token, and returns the answer', async () => {
    const fetchMock = sequence(jsonResponse(201, { id: 'a' }))
    setCsrfToken('token-1')
    const file = new File(['hello'], 'hello.txt', { type: 'text/plain' })

    expect(await uploadFile('/api/pages/p/attachments?name=hello.txt', file)).toEqual({ id: 'a' })

    const [path, init] = fetchMock.mock.calls[0]!
    expect(path).toBe('/api/pages/p/attachments?name=hello.txt')
    expect(init.method).toBe('POST')
    expect(init.body).toBe(file)
    // Not the file's own type: the server works the type out, and takes nothing but raw bytes.
    expect(init.headers).toEqual({
      'Content-Type': 'application/octet-stream',
      'X-CSRF-Token': 'token-1',
    })
  })

  it('sends the same file again after fetching a new token', async () => {
    const fetchMock = sequence(
      jsonResponse(400, { code: 'csrf_invalid' }),
      jsonResponse(200, { csrfToken: 'token-2' }),
      jsonResponse(201, { id: 'a' }),
    )
    setCsrfToken('token-1')
    const file = new Blob(['x'])

    await uploadFile('/api/up', file)

    expect(fetchMock.mock.calls[2]![1].body).toBe(file)
    expect(fetchMock.mock.calls[2]![1].headers['X-CSRF-Token']).toBe('token-2')
  })

  it('turns a refusal into its code', async () => {
    sequence(jsonResponse(413, { code: 'attachment_too_large' }))

    expect(await uploadFile('/api/up', new Blob(['x'])).catch((e) => e)).toMatchObject({
      status: 413,
      code: 'attachment_too_large',
    })
  })
})

describe('api and waiting times', () => {
  it('keeps how long to wait when the server says so', async () => {
    stubApi([
      {
        method: 'POST',
        path: '/api/x',
        respond: () =>
          new Response(JSON.stringify({ code: 'login_locked' }), {
            status: 429,
            headers: { 'Content-Type': 'application/json', 'Retry-After': '120' },
          }),
      },
    ])

    expect(await api('POST', '/api/x', {}).catch((e) => e)).toMatchObject({
      status: 429,
      code: 'login_locked',
      retryAfterSeconds: 120,
    })
  })

  it('has no wait when there is no header, or a bad one', async () => {
    stubApi([
      {
        method: 'POST',
        path: '/a',
        respond: () => jsonResponse(401, { code: 'invalid_credentials' }),
      },
      {
        method: 'POST',
        path: '/b',
        respond: () =>
          new Response('{"code":"x"}', { status: 429, headers: { 'Retry-After': 'soon' } }),
      },
    ])

    expect((await api('POST', '/a', {}).catch((e) => e)).retryAfterSeconds).toBeUndefined()
    expect((await api('POST', '/b', {}).catch((e) => e)).retryAfterSeconds).toBeUndefined()
  })
})

describe('uploadFileWithProgress', () => {
  const file = new File(['hello'], 'export.jex')
  let fetchMock: ReturnType<typeof vi.fn>

  /** The sign-in state the upload asks for first, with this token in it; one answer per call, the last one repeated. */
  function tokens(...values: string[]) {
    fetchMock = vi.fn(async () =>
      jsonResponse(200, { csrfToken: values.length > 1 ? values.shift() : values[0] }),
    )
    vi.stubGlobal('fetch', fetchMock)
  }

  function upload(
    onProgress: (sent: number, total: number) => void = () => {},
    signal?: AbortSignal,
  ) {
    return uploadFileWithProgress<{ id: string }>('/api/x', file, onProgress, signal)
  }

  /** The request with this number, once it has been sent: the token is asked for first. */
  async function sent(index = 0) {
    await vi.waitFor(() => expect(FakeXhr.sent.length).toBeGreaterThan(index))
    return FakeXhr.sent[index]!
  }

  beforeEach(() => {
    stubXhr()
    tokens('fresh')
  })

  it('sends the file as raw bytes with a fresh token, reports progress, and returns the answer', async () => {
    setCsrfToken('old')
    const progress: [number, number][] = []

    const result = upload((s, total) => progress.push([s, total]))
    const request = await sent()
    request.progress(2, 5)
    request.progress(5, 5)
    request.respond(202, { id: 'a' })

    expect(await result).toEqual({ id: 'a' })
    expect(fetchMock.mock.calls[0]![0]).toBe('/api/auth/state')
    expect(request.method).toBe('POST')
    expect(request.url).toBe('/api/x')
    expect(request.body).toBe(file)
    expect(request.headers).toEqual({
      'Content-Type': 'application/octet-stream',
      'X-CSRF-Token': 'fresh',
    })
    expect(progress).toEqual([
      [2, 5],
      [5, 5],
    ])
  })

  it('still sends the file with the token it has when the state cannot be asked for', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    setCsrfToken('kept')

    const result = upload()
    const request = await sent()
    request.respond(202, { id: 'a' })

    expect(await result).toEqual({ id: 'a' })
    expect(request.headers['X-CSRF-Token']).toBe('kept')
  })

  it('turns an error answer into an ApiError, with how long to wait', async () => {
    const result = upload()
    ;(await sent()).respond(429, { code: 'too_many_requests' }, { 'Retry-After': '30' })

    expect(await result.catch((e) => e)).toMatchObject({
      status: 429,
      code: 'too_many_requests',
      retryAfterSeconds: 30,
    })
  })

  it('reports a failed connection as a network error', async () => {
    const result = upload()
    ;(await sent()).fail()

    expect(await result.catch((e) => e)).toMatchObject({ status: 0, code: 'network_error' })
  })

  it('fetches a new token and sends the file again once when the token is out of date', async () => {
    tokens('token-1', 'token-2')

    const result = upload()
    ;(await sent(0)).respond(400, { code: 'csrf_invalid' })
    ;(await sent(1)).respond(202, { id: 'b' })

    expect(await result).toEqual({ id: 'b' })
    expect(FakeXhr.sent[0]!.headers['X-CSRF-Token']).toBe('token-1')
    expect(FakeXhr.sent[1]!.headers['X-CSRF-Token']).toBe('token-2')
  })

  it('does not repeat the upload for any other 400', async () => {
    const result = upload()
    ;(await sent()).respond(400, { code: 'import_invalid' })

    expect(await result.catch((e) => e)).toMatchObject({ code: 'import_invalid' })
    expect(FakeXhr.sent).toHaveLength(1)
  })

  it('ends with an AbortError, not an ApiError, when the upload is cancelled', async () => {
    const controller = new AbortController()
    const result = upload(() => {}, controller.signal)
    const request = await sent()

    controller.abort()

    const error = await result.catch((e) => e)
    expect(error).toBeInstanceOf(DOMException)
    expect(error.name).toBe('AbortError')
    expect(request.aborted).toBe(true)
  })

  it('lets go of the signal once the upload has ended', async () => {
    const controller = new AbortController()
    const result = upload(() => {}, controller.signal)
    const request = await sent()
    request.respond(202, { id: 'a' })
    await result

    controller.abort()

    expect(request.aborted).toBe(false)
  })

  it('does not send anything when it was cancelled already', async () => {
    const controller = new AbortController()
    controller.abort()

    expect((await upload(() => {}, controller.signal).catch((e) => e)).name).toBe('AbortError')
    expect(FakeXhr.sent).toHaveLength(0)
  })
})
