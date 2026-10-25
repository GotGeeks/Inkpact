import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api'
import { freshInstall, ok, refused, signedIn, signedOut, state, stubApi } from '../testing/helpers'
import { useAuthStore } from './auth'

beforeEach(() => setActivePinia(createPinia()))
afterEach(() => vi.unstubAllGlobals())

describe('auth store', () => {
  it('starts unloaded and signed out', () => {
    const auth = useAuthStore()

    expect(auth.loaded).toBe(false)
    expect(auth.user).toBeNull()
  })

  it('loads the state from the server', async () => {
    stubApi([state(signedIn)])
    const auth = useAuthStore()

    await auth.load()

    expect(auth.loaded).toBe(true)
    expect(auth.setupRequired).toBe(false)
    expect(auth.user?.displayName).toBe('Jos')
  })

  it('knows a fresh install needs setup', async () => {
    stubApi([state(freshInstall)])
    const auth = useAuthStore()

    await auth.load()

    expect(auth.setupRequired).toBe(true)
    expect(auth.user).toBeNull()
  })

  it('stays unloaded when the server cannot be reached', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    const auth = useAuthStore()

    await expect(auth.load()).rejects.toMatchObject({ code: 'network_error' })
    expect(auth.loaded).toBe(false)
  })

  it('logs in with the email and password, then reloads who is signed in', async () => {
    let signedInNow = false
    const fetchMock = stubApi([
      {
        method: 'GET',
        path: '/api/auth/state',
        respond: () =>
          new Response(JSON.stringify(signedInNow ? signedIn : signedOut), { status: 200 }),
      },
      {
        method: 'POST',
        path: '/api/auth/login',
        respond: () => {
          signedInNow = true
          return new Response(null, { status: 204 })
        },
      },
    ])
    const auth = useAuthStore()
    await auth.load()

    await auth.login('jos@example.com', 'secret secret secret')

    expect(JSON.parse(fetchMock.mock.calls[1]![1].body)).toEqual({
      email: 'jos@example.com',
      password: 'secret secret secret',
    })
    expect(auth.user?.email).toBe('jos@example.com')
  })

  it('stays signed out when the login is refused', async () => {
    stubApi([state(signedOut), refused('POST', '/api/auth/login', 401, 'invalid_credentials')])
    const auth = useAuthStore()
    await auth.load()

    const error = await auth.login('jos@example.com', 'wrong').catch((e) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error.code).toBe('invalid_credentials')
    expect(auth.user).toBeNull()
  })

  it('sends the whole setup form', async () => {
    const fetchMock = stubApi([state(freshInstall), ok('POST', '/api/auth/setup')])
    const auth = useAuthStore()
    const form = {
      setupCode: 'ABCD2345',
      email: 'jos@example.com',
      displayName: 'Jos',
      password: 'correct horse battery',
      workspaceName: 'Private',
    }

    await auth.setup(form)

    expect(JSON.parse(fetchMock.mock.calls[0]![1].body)).toEqual(form)
  })

  it.each([
    ['logout', '/api/auth/logout'],
    ['logoutEverywhere', '/api/auth/logout-everywhere'],
  ] as const)('%s calls the server and reloads the state', async (action, path) => {
    let signedInNow = true
    stubApi([
      {
        method: 'GET',
        path: '/api/auth/state',
        respond: () =>
          new Response(JSON.stringify(signedInNow ? signedIn : signedOut), { status: 200 }),
      },
      {
        method: 'POST',
        path,
        respond: () => {
          signedInNow = false
          return new Response(null, { status: 204 })
        },
      },
    ])
    const auth = useAuthStore()
    await auth.load()
    expect(auth.user).not.toBeNull()

    await auth[action]()

    expect(auth.user).toBeNull()
  })
})
