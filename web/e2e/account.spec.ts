import type { Page } from '@playwright/test'
import { SIGNED_OUT } from './credentials'
import { expect, test } from './fixtures'
import {
  closeDrawer,
  expectHome,
  expectLoginPage,
  openSidebar,
  openUserMenu,
  signInThroughTheForm,
} from './helpers'

/**
 * The account section of the settings (run B.9). Every device project shares one account and its session, so nothing
 * here changes the account for good or ends a session it did not make: the device list signs out only a second session
 * this test makes itself, and the name and "all other devices" are answered with page.route. Their real effect is in
 * the server tests (AccountEndpointTests, SyncHubTests).
 */

async function openSettings(page: Page) {
  await page.goto('/')
  await openUserMenu(page)
  await page.getByRole('menuitem', { name: 'Settings', exact: true }).click()
  await closeDrawer(page)
  await expect(page.getByRole('heading', { level: 2, name: 'Your account' })).toBeVisible()
}

test('another browser of the account is listed, and signed out from here', async ({
  page,
  browser,
}) => {
  const other = await browser.newContext({ storageState: SIGNED_OUT })
  const otherPage = await other.newPage()
  await signInThroughTheForm(otherPage)
  await expectHome(otherPage)
  // That browser's own session, by its handle: the only one this test may end.
  const sessions = (await (await otherPage.request.get('/api/account/sessions')).json()) as {
    sessions: { id: string; current: boolean }[]
  }
  const handle = sessions.sessions.find((session) => session.current)!.id

  await openSettings(page)
  const signOut = page.locator(`[id="sign-out-${handle}"]`)
  await expect(signOut).toBeVisible()
  await signOut.click()
  const confirm = page.locator(`[aria-labelledby="confirm-${handle}"]`)
  await expect(confirm.getByText('Sign this device out?')).toBeFocused()
  await confirm.getByRole('button', { name: 'Sign out', exact: true }).click()

  await expect(page.getByTestId('devices-done')).toContainText('is signed out.')
  await expect(page.locator(`[id="sign-out-${handle}"]`)).toHaveCount(0)
  // The other browser's next request finds its session gone; this one is still signed in.
  await otherPage.reload()
  await expectLoginPage(otherPage)
  await expect(page.getByRole('heading', { level: 2, name: 'Your account' })).toBeVisible()
  await other.close()
})

test('this device is marked as this device, with no button to sign it out here', async ({
  page,
}) => {
  await openSettings(page)

  const mine = page.getByTestId('device').filter({ hasText: 'This device' })
  await expect(mine).toHaveCount(1)
  await expect(mine.getByRole('button')).toHaveCount(0)
  await expect(page.getByTestId('devices')).toContainText('Pages it saved for offline use')
})

test('a new display name is saved and is the menu’s name at once', async ({ page }) => {
  const state = (await (await page.request.get('/api/auth/state')).json()) as {
    user: { id: string; email: string; isAdmin: boolean }
  }
  let sent: unknown = null
  await page.route('**/api/account', async (route) => {
    if (route.request().method() !== 'PATCH') return route.fallback()
    sent = route.request().postDataJSON()
    await route.fulfill({ json: { ...state.user, displayName: 'Renamed for a moment' } })
  })
  await openSettings(page)

  const account = page.getByTestId('account')
  await account.getByLabel('Your name').fill('  Renamed for a moment  ')
  await account.getByRole('button', { name: 'Save', exact: true }).click()

  await expect(account.getByRole('status')).toHaveText('Saved.')
  expect(sent).toEqual({ displayName: '  Renamed for a moment  ' })
  await openSidebar(page)
  await expect(page.getByTestId('user-name')).toHaveText('Renamed for a moment')
})

test('signing out every other device asks first, then says how many', async ({ page }) => {
  // Answered here: for real it would end the session every device project shares.
  const now = new Date().toISOString()
  const listed = {
    sessions: [
      { id: 'this', signedInAt: now, lastSeenAt: now, userAgent: null, current: true },
      { id: 'phone', signedInAt: now, lastSeenAt: now, userAgent: null, current: false },
    ],
    more: 0,
  }
  let posted = false
  await page.route('**/api/account/sessions', (route) => route.fulfill({ json: listed }))
  await page.route('**/api/account/sessions/sign-out-others', async (route) => {
    posted = true
    listed.sessions = listed.sessions.filter((session) => session.current)
    await route.fulfill({ json: { ended: 1 } })
  })
  await openSettings(page)
  const devices = page.getByTestId('devices')

  await devices.getByRole('button', { name: 'Sign out all other devices' }).click()
  await expect(devices.getByText('Sign out every device but this one?')).toBeFocused()
  expect(posted).toBe(false)
  await devices.getByRole('button', { name: 'Sign out all other devices' }).click()

  await expect(page.getByTestId('devices-done')).toHaveText('1 other device is signed out.')
  expect(posted).toBe(true)
  await expect(page.getByTestId('device')).toHaveCount(1)
})
