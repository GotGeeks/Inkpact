import { expect, test } from './fixtures'
import { expectHome, signInThroughTheForm } from './helpers'
import { openSyncSocket } from './syncSocket'

// A browser treats localhost as a secure origin. On a plain-HTTP LAN address, which is how many self-hosters
// run Inkpact, a policy such as upgrade-insecure-requests would turn every request into an https one that
// nothing answers, and a Secure-only cookie would never be stored. This runs the app under a name that is
// not localhost, and signs in there.
test('signing in works over plain http on a non-localhost name', async ({
  page,
  context,
  baseURL,
}) => {
  expect(new URL(baseURL!).hostname).not.toBe('localhost')
  expect(new URL(baseURL!).protocol).toBe('http:')
  const failed: string[] = []
  page.on('requestfailed', (request) => failed.push(request.url()))

  await signInThroughTheForm(page)
  await expectHome(page)

  // Without HTTPS the cookies have plain names and no Secure flag, so the browser keeps them.
  const cookies = await context.cookies()
  expect(cookies.map((cookie) => cookie.name).sort()).toEqual(['inkpact', 'inkpact-csrf'])
  expect(cookies.find((cookie) => cookie.name === 'inkpact')).toMatchObject({
    httpOnly: true,
    secure: false,
    sameSite: 'Lax',
  })
  expect(cookies.find((cookie) => cookie.name === 'inkpact-csrf')).toMatchObject({
    httpOnly: true,
    secure: false,
    sameSite: 'Strict',
  })

  await page.reload()
  await expectHome(page)
  expect(failed.filter((url) => url.startsWith('https:'))).toEqual([])

  // The sync socket is ws:, not wss:, and still allowed by connect-src 'self'.
  expect(await openSyncSocket(page, 'sync')).toMatchObject({ open: true, closed: null })
})
