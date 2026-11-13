import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { jsonResponse } from '../testing/helpers'
import { useAuthStore } from './auth'
import { isReleasePage, useVersionStore } from './version'

const notes = 'https://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0'

function signIn(id: string, isAdmin = true) {
  useAuthStore().user = { id, email: `${id}@example.com`, displayName: id, isAdmin }
}

function server(answer: () => Response) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input) !== '/api/version') throw new Error(`Unexpected request: ${String(input)}`)
    return answer()
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

beforeEach(() => setActivePinia(createPinia()))
afterEach(() => vi.unstubAllGlobals())

describe('isReleasePage', () => {
  it.each([
    [notes, true],
    ['https://github.com/GotGeeks/Inkpact/releases/latest', true],
    ['http://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0', false],
    ['https://github.com/GotGeeks/Inkpact/issues/1', false],
    ['https://github.com/GotGeeks/Inkpact/releases-evil/x', false],
    ['https://github.com.evil.example/GotGeeks/Inkpact/releases/tag/v1', false],
    ['https://user@github.com/GotGeeks/Inkpact/releases/tag/v1', false],
    [`${notes}?x=1`, false],
    [`${notes}#x`, false],
    ['javascript:alert(1)', false],
    ['not a url', false],
  ])('%s is a release page: %s', (url, expected) => {
    expect(isReleasePage(url)).toBe(expected)
  })
})

describe('useVersionStore', () => {
  it('reads the version once for the person signed in', async () => {
    signIn('a')
    const fetchMock = server(() =>
      jsonResponse(200, { version: '0.1.0', update: { version: '0.2.0', url: notes } }),
    )
    const versions = useVersionStore()

    await Promise.all([versions.load(), versions.load()])
    await versions.load()

    expect(fetchMock).toHaveBeenCalledOnce()
    expect(versions.info).toEqual({ version: '0.1.0', update: { version: '0.2.0', url: notes } })
  })

  it('reads it again when asked, and for someone else who signs in', async () => {
    signIn('a')
    const fetchMock = server(() => jsonResponse(200, { version: '0.1.0', update: null }))
    const versions = useVersionStore()
    await versions.load()

    await versions.load(true)
    signIn('b', false)
    await versions.load()

    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('shows no update whose notes are not a release page of Inkpact', async () => {
    signIn('a')
    server(() =>
      jsonResponse(200, {
        version: '0.1.0',
        update: { version: '0.2.0', url: 'https://evil.example/' },
      }),
    )
    const versions = useVersionStore()

    await versions.load()

    expect(versions.info).toEqual({ version: '0.1.0', update: null })
  })

  it('keeps the error code of a read that failed', async () => {
    signIn('a')
    server(() => jsonResponse(500, { code: 'server_error' }))
    const versions = useVersionStore()

    await versions.load()

    expect(versions.error).toBe('server_error')
    expect(versions.info).toBeNull()
  })

  it('asks nothing when nobody is signed in', async () => {
    const fetchMock = server(() => jsonResponse(200, { version: '0.1.0', update: null }))

    await useVersionStore().load()

    expect(fetchMock).not.toHaveBeenCalled()
  })
})
