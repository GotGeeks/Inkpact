import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { jsonResponse } from '../testing/helpers'
import { useAuthStore } from './auth'
import { isSafeLink, useVersionStore, type VersionInfo } from './version'

const notes = 'https://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0'
const plain: VersionInfo = { version: '0.1.0', development: false, update: null }
const withUpdate: VersionInfo = { ...plain, update: { version: '0.2.0', url: notes } }

function signIn(id: string, isAdmin = true) {
  useAuthStore().user = { id, email: `${id}@example.com`, displayName: id, isAdmin }
}

function server(answer: () => Response | Promise<Response>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input) !== '/api/version') throw new Error(`Unexpected request: ${String(input)}`)
    return answer()
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

beforeEach(() => setActivePinia(createPinia()))
afterEach(() => vi.unstubAllGlobals())

// Which pages are release notes is the server's rule (UpdateCheckTests); the client refuses only what no link should be.
describe('isSafeLink', () => {
  it.each([
    [notes, true],
    ['http://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0', false],
    ['javascript:alert(1)', false],
    ['data:text/html,x', false],
    ['not a url', false],
  ])('%s may be a link: %s', (url, expected) => {
    expect(isSafeLink(url)).toBe(expected)
  })
})

describe('useVersionStore', () => {
  it('reads the version once for the person signed in', async () => {
    signIn('a')
    const fetchMock = server(() => jsonResponse(200, withUpdate))
    const versions = useVersionStore()

    await Promise.all([versions.load(), versions.load()])
    await versions.load()

    expect(fetchMock).toHaveBeenCalledOnce()
    expect(versions.info).toEqual(withUpdate)
  })

  it('takes the server’s word on a development build', async () => {
    signIn('a')
    server(() => jsonResponse(200, { version: '0.0.0-dev', development: true, update: null }))
    const versions = useVersionStore()

    await versions.load()

    expect(versions.info?.development).toBe(true)
  })

  it('reads it again when asked, and for someone else who signs in', async () => {
    signIn('a')
    const fetchMock = server(() => jsonResponse(200, plain))
    const versions = useVersionStore()
    await versions.load()

    await versions.load(true)
    signIn('b', false)
    await versions.load()

    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('does not hand one person’s answer to the next who signs in while it is on its way', async () => {
    signIn('a')
    let answerFirst: (response: Response) => void = () => {}
    const first = new Promise<Response>((resolve) => (answerFirst = resolve))
    let calls = 0
    const fetchMock = server(() => (++calls === 1 ? first : jsonResponse(200, plain)))
    const versions = useVersionStore()
    const forA = versions.load()

    signIn('b')
    const forB = versions.load()
    answerFirst(jsonResponse(200, { ...plain, update: { version: '9.9.9', url: notes } }))
    await Promise.all([forA, forB])

    // B asked for themself, and got their own answer; A's arrived after and was dropped.
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(versions.info).toEqual(plain)
    expect(versions.loading).toBe(false)
  })

  it('shows no update whose link could run script', async () => {
    signIn('a')
    server(() =>
      jsonResponse(200, { ...plain, update: { version: '0.2.0', url: 'javascript:alert(1)' } }),
    )
    const versions = useVersionStore()

    await versions.load()

    expect(versions.info).toEqual(plain)
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
    const fetchMock = server(() => jsonResponse(200, plain))

    await useVersionStore().load()

    expect(fetchMock).not.toHaveBeenCalled()
  })
})
