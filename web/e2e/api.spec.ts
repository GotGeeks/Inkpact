import { expect, test } from './fixtures'

test('the health endpoint answers with json and the security headers', async ({ request }) => {
  const response = await request.get('/api/health')

  expect(response.status()).toBe(200)
  expect(response.headers()['content-type']).toContain('application/json')
  expect(response.headers()['content-security-policy']).toContain("default-src 'self'")
  expect(await response.json()).toEqual({ status: 'ok' })
})

test('an unknown api path is a json 404, never the app shell', async ({ request }) => {
  const response = await request.get('/api/nope')

  expect(response.status()).toBe(404)
  expect(response.headers()['content-type']).toContain('application/json')
  expect(await response.json()).toEqual({ code: 'not_found' })
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
