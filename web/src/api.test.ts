import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api } from './api'
import { jsonResponse, stubApi } from './testing/helpers'

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
