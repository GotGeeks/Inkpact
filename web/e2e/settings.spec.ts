import { expect, test } from './fixtures'
import { closeDrawer, openUserMenu } from './helpers'

/**
 * The settings screen (run B.5b): reached from the account menu, it shows the workspace's history policy as the server
 * has it, says what is wrong with numbers it cannot save, and warns before a change that removes versions. Nothing is
 * saved here: every device project shares the workspace, and history-policy.spec.ts already saves and puts back.
 */

test('the settings are reached from the account menu and show the history policy the server has', async ({
  page,
}) => {
  await page.goto('/')
  await openUserMenu(page)
  await page.getByRole('menuitem', { name: 'Settings', exact: true }).click()
  await closeDrawer(page)

  await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible()
  const workspaces = (await (await page.request.get('/api/workspaces')).json()) as { id: string }[]
  const policy = (await (
    await page.request.get(`/api/workspaces/${workspaces[0]!.id}/history-policy`)
  ).json()) as { granularHours: number; dailyDays: number; expiryDays: number }
  await expect(page.getByLabel('Every version, for (hours)')).toHaveValue(
    String(policy.granularHours),
  )
  await expect(page.getByLabel('Then the newest of each day, until (days)')).toHaveValue(
    String(policy.dailyDays),
  )
  await expect(page.getByTestId('history-usage')).toContainText('Versions take')
})

test('the settings say which Inkpact runs', async ({ page }) => {
  await page.goto('/')
  await openUserMenu(page)
  await page.getByRole('menuitem', { name: 'Settings', exact: true }).click()
  await closeDrawer(page)

  // The e2e server is built without a version, as a developer's build is, and never asks GitHub.
  await expect(page.getByTestId('app-version')).toHaveText(
    'A development build, without a version.',
  )
  await expect(page.getByTestId('update')).toHaveCount(0)
})

test('the administrator is told of a newer release, in the menu and in the settings', async ({
  page,
}) => {
  // Answered before the app loads: on WebKit a reload is served by the service worker, past page.route.
  const notes = 'https://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0'
  await page.route('**/api/version', (route) =>
    route.fulfill({ json: { version: '0.1.0', update: { version: '0.2.0', url: notes } } }),
  )
  await page.goto('/')
  await openUserMenu(page)
  await expect(page.getByText('Inkpact 0.2.0 is out')).toBeVisible()
  await page.getByRole('menuitem', { name: 'Settings', exact: true }).click()
  await closeDrawer(page)

  await expect(page.getByTestId('app-version')).toHaveText('Version 0.1.0')
  const update = page.getByTestId('update')
  await expect(update.getByRole('heading', { name: 'Inkpact 0.2.0 is out' })).toBeVisible()
  await expect(update.getByRole('link', { name: /What is new in 0\.2\.0/ })).toHaveAttribute(
    'href',
    notes,
  )
})

test('numbers that cannot be saved are named, and a change that removes versions asks first', async ({
  page,
}) => {
  let saved = false
  await page.route('**/history-policy', async (route) => {
    if (route.request().method() === 'PUT') saved = true
    await route.fallback()
  })
  await page.goto('/')
  await openUserMenu(page)
  await page.getByRole('menuitem', { name: 'Settings', exact: true }).click()
  await closeDrawer(page)
  const daily = page.getByLabel('Then the newest of each day, until (days)')
  await expect(daily).not.toHaveValue('')
  const before = await daily.inputValue()

  await daily.fill('0')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByTestId('policy-problem')).toHaveText(
    'Keep one a day for a whole number of days, from 1 to 3650.',
  )

  await daily.fill(String(Math.max(1, Number(before) - 1)))
  const granular = page.getByLabel('Every version, for (hours)')
  await granular.fill(
    String(Math.min(Number(await granular.inputValue()), Math.max(1, Number(before) - 1) * 24)),
  )
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  const warning = page.getByTestId('policy-warning')
  await expect(warning.getByRole('heading', { name: 'Remove versions?' })).toBeFocused()
  await warning.getByRole('button', { name: 'Cancel' }).click()
  await expect(warning).toBeHidden()
  expect(saved).toBe(false)
})
