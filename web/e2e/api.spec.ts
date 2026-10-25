import { request as playwrightRequest, type APIRequestContext } from '@playwright/test'
import { SIGNED_OUT } from './credentials'
import { expect, test } from './fixtures'

// A request context without the project's session. Without the explicit empty state, Playwright would give it
// the saved sign-in of the project, and "signing out" below would really sign out everyone.
const stranger = (baseURL: string | undefined) =>
  playwrightRequest.newContext({ baseURL, storageState: SIGNED_OUT })

/** What the web client does first: ask for the state, which also sets the CSRF cookie and hands out the token. */
async function csrfToken(visitor: APIRequestContext): Promise<string> {
  const state = (await (await visitor.get('/api/auth/state')).json()) as { csrfToken: string }
  return state.csrfToken
}

// The request fixture carries the project's session; these checks are about what an anonymous caller gets.
test.describe('as a stranger', () => {
  test('the health endpoint answers with json and the security headers', async ({ baseURL }) => {
    const visitor = await stranger(baseURL)
    const response = await visitor.get('/api/health')

    expect(response.status()).toBe(200)
    expect(response.headers()['content-type']).toContain('application/json')
    expect(response.headers()['content-security-policy']).toContain("default-src 'self'")
    expect(response.headers()['cache-control']).toBe('no-store')
    expect(await response.json()).toEqual({ status: 'ok' })
    await visitor.dispose()
  })

  test('an unknown api path is a json 404, never the app shell', async ({ baseURL }) => {
    const visitor = await stranger(baseURL)
    const response = await visitor.get('/api/nope')

    expect(response.status()).toBe(404)
    expect(response.headers()['content-type']).toContain('application/json')
    expect(await response.json()).toEqual({ code: 'not_found' })
    await visitor.dispose()
  })

  test('the state says who is signed in: nobody, and hands out a token', async ({ baseURL }) => {
    const visitor = await stranger(baseURL)
    const response = await visitor.get('/api/auth/state')

    expect(response.status()).toBe(200)
    expect(response.headers()['cache-control']).toBe('no-store')
    const state = await response.json()
    expect(state).toMatchObject({ setupRequired: false, user: null })
    expect(state.csrfToken).toEqual(expect.any(String))
    const cookies = (await visitor.storageState()).cookies
    expect(cookies.map((cookie) => cookie.name)).toEqual(['inkpact-csrf'])
    expect(cookies[0]).toMatchObject({ httpOnly: true, sameSite: 'Strict' })
    await visitor.dispose()
  })

  test('a change without the token is refused, even one that would be refused anyway', async ({
    baseURL,
  }) => {
    const visitor = await stranger(baseURL)
    await csrfToken(visitor) // the cookie is there; the header is not

    const response = await visitor.post('/api/auth/login', {
      data: { email: 'admin@example.com', password: 'whatever it is' },
    })

    expect(response.status()).toBe(400)
    expect(await response.json()).toEqual({ code: 'csrf_invalid' })
    await visitor.dispose()
  })

  test('signing out without a session is a json 401, not a redirect', async ({ baseURL }) => {
    const visitor = await stranger(baseURL)
    const token = await csrfToken(visitor)

    const response = await visitor.post('/api/auth/logout', {
      data: {},
      headers: { 'X-CSRF-Token': token },
      maxRedirects: 0,
    })

    expect(response.status()).toBe(401)
    expect(await response.json()).toEqual({ code: 'not_authenticated' })
    await visitor.dispose()
  })

  test('a form post is refused: state changes must be json', async ({ baseURL }) => {
    const visitor = await stranger(baseURL)
    const response = await visitor.post('/api/auth/login', {
      form: { email: 'admin@example.com', password: 'whatever it is' },
    })

    expect(response.status()).toBe(415)
    expect(await response.json()).toEqual({ code: 'unsupported_media_type' })
    await visitor.dispose()
  })

  test('setup is closed: another administrator cannot be added', async ({ baseURL }) => {
    const visitor = await stranger(baseURL)
    const token = await csrfToken(visitor)

    const response = await visitor.post('/api/auth/setup', {
      headers: { 'X-CSRF-Token': token },
      data: {
        setupCode: 'E2ESETUP',
        email: 'second@example.com',
        displayName: 'Second',
        password: 'correct horse battery staple',
        workspaceName: 'Mine now',
      },
    })

    expect(response.status()).toBe(409)
    expect(await response.json()).toEqual({ code: 'setup_not_available' })
    await visitor.dispose()
  })

  test('workspaces need a signed-in user', async ({ baseURL }) => {
    const visitor = await stranger(baseURL)

    expect((await visitor.get('/api/workspaces')).status()).toBe(401)
    expect(
      (await visitor.get('/api/workspaces/00000000-0000-0000-0000-000000000000')).status(),
    ).toBe(401)
    await visitor.dispose()
  })
})

test('the signed-in user sees their own workspace, and only that', async ({ request }) => {
  const list = await request.get('/api/workspaces')
  expect(list.status()).toBe(200)
  const workspaces = (await list.json()) as { id: string; name: string; role: string }[]
  expect(workspaces).toEqual([{ id: expect.any(String), name: 'E2E workspace', role: 'owner' }])

  const one = await request.get(`/api/workspaces/${workspaces[0]!.id}`)
  expect(await one.json()).toEqual(workspaces[0])

  // A workspace that is not theirs looks exactly like one that does not exist.
  const other = await request.get('/api/workspaces/11111111-1111-1111-1111-111111111111')
  expect(other.status()).toBe(404)
  expect(await other.json()).toEqual({ code: 'not_found' })
})

test('a missing asset is a 404, never the app shell', async ({ request }) => {
  const response = await request.get('/assets/missing.js')

  expect(response.status()).toBe(404)
  expect(await response.text()).not.toContain('<html')
})

test('built assets are cached for good, the shell never', async ({ request }) => {
  const shell = await request.get('/')
  expect(shell.headers()['cache-control']).toBe('no-cache')

  const script = /src="(\/assets\/[^"]+\.js)"/.exec(await shell.text())![1]!
  const asset = await request.get(script)
  expect(asset.status()).toBe(200)
  expect(asset.headers()['cache-control']).toContain('immutable')
})
