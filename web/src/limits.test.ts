import { afterEach, describe, expect, it, vi } from 'vitest'
import { cachedLimit } from './limits'
import { jsonResponse } from './testing/helpers'

afterEach(() => vi.unstubAllGlobals())

describe('cachedLimit', () => {
  it('asks the server once, and keeps the answer', async () => {
    const fetchMock = vi.fn<(path: string) => Promise<Response>>(async () =>
      jsonResponse(200, { maxFileSize: 1234 }),
    )
    vi.stubGlobal('fetch', fetchMock)
    const limit = cachedLimit('/api/x/limits')

    expect(await limit.get()).toBe(1234)
    expect(await limit.get()).toBe(1234)

    expect(fetchMock).toHaveBeenCalledOnce()
    expect(fetchMock.mock.calls[0]![0]).toBe('/api/x/limits')
  })

  it('answers null when it could not ask, and asks again next time', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(500, { code: 'server_error' }))
      .mockResolvedValue(jsonResponse(200, { maxFileSize: 99 }))
    vi.stubGlobal('fetch', fetchMock)
    const limit = cachedLimit('/api/x/limits')

    expect(await limit.get()).toBeNull()
    expect(await limit.get()).toBe(99)
  })

  it('asks again after forget, and each limit is kept apart', async () => {
    const fetchMock = vi.fn(async (path: string) => jsonResponse(200, { maxFileSize: path.length }))
    vi.stubGlobal('fetch', fetchMock)
    const short = cachedLimit('/a')
    const long = cachedLimit('/abc')

    expect([await short.get(), await long.get()]).toEqual([2, 4])
    short.forget()
    await short.get()

    expect(fetchMock).toHaveBeenCalledTimes(3)
  })
})
