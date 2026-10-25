import { expect, test } from './fixtures'

// A browser treats localhost as a secure origin. On a plain-HTTP LAN address, which is how many self-hosters
// run Inkpact, a policy such as upgrade-insecure-requests would turn every request into an https one that
// nothing answers, and the page would stay blank. This runs the app under a name that is not localhost.
test('the app works over plain http on a non-localhost name', async ({ page, baseURL }) => {
  expect(new URL(baseURL!).hostname).not.toBe('localhost')
  expect(new URL(baseURL!).protocol).toBe('http:')

  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'Inkpact', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Nederlands' }).click()
  await expect(page.getByText('Notities die synchroniseren, zonder limieten.')).toBeVisible()
})
