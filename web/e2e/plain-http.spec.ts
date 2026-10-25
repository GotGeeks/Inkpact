import { expect, test } from './fixtures'
import { expectHome, signInThroughTheForm } from './helpers'

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

  // Without HTTPS the cookie has the plain name and no Secure flag, so the browser keeps it.
  const cookies = await context.cookies()
  expect(cookies.map((cookie) => cookie.name)).toEqual(['inkpact'])
  expect(cookies[0]).toMatchObject({ httpOnly: true, secure: false, sameSite: 'Lax' })

  await page.reload()
  await expectHome(page)
  expect(failed.filter((url) => url.startsWith('https:'))).toEqual([])
})
