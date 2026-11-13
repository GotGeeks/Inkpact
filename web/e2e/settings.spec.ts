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
