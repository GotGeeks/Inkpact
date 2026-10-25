import { request as playwrightRequest } from '@playwright/test'
import { SIGNED_OUT } from './credentials'
import { expect, test } from './fixtures'

// A request context without the project's session. Without the explicit empty state, Playwright would give it
// the saved sign-in of the project, and "signing out" below would really sign out everyone.
const stranger = (baseURL: string | undefined) =>
  playwrightRequest.newContext({ baseURL, storageState: SIGNED_OUT })

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

  test('the state says who is signed in: nobody', async ({ baseURL }) => {
    const visitor = await stranger(baseURL)
    const response = await visitor.get('/api/auth/state')

    expect(response.status()).toBe(200)
    expect(await response.json()).toEqual({ setupRequired: false, user: null })
    expect(response.headers()['cache-control']).toBe('no-store')
    await visitor.dispose()
  })

  test('signing out without a session is a json 401, not a redirect', async ({ baseURL }) => {
    const visitor = await stranger(baseURL)
    const response = await visitor.post('/api/auth/logout', { data: {}, maxRedirects: 0 })

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
    const response = await visitor.post('/api/auth/setup', {
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
