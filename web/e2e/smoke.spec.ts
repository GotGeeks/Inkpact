import { SIGNED_OUT } from './credentials'
import { expect, test } from './fixtures'
import { expectLoginPage } from './helpers'

// What a visitor sees first: the login page. Signed-in behaviour is in session.spec.ts.
test.use({ storageState: SIGNED_OUT })

test('the login page renders in English', async ({ page }) => {
  await page.goto('/login')

  await expect(page).toHaveTitle('Inkpact')
  await expectLoginPage(page)
  await expect(page.getByText('Notes that sync, without limits.')).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')
})

test('the language switch changes the text, with the keyboard', async ({ page, isMobile }) => {
  test.skip(isMobile, 'Focus and Space are for keyboards')
  await page.goto('/login')

  const dutch = page.getByRole('button', { name: 'Nederlands' })
  await dutch.focus()
  await page.keyboard.press('Space')

  await expect(page.getByRole('heading', { level: 1, name: 'Inloggen' })).toBeVisible()
  await expect(page.getByText('Notities die synchroniseren, zonder limieten.')).toBeVisible()
  await expect(page.getByRole('group', { name: 'Taal' })).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('lang', 'nl')
})

test('the language switch changes the text, by pointer or touch', async ({ page }) => {
  await page.goto('/login')

  await page.getByRole('button', { name: 'Nederlands' }).click()
  await expect(page.getByText('Notities die synchroniseren, zonder limieten.')).toBeVisible()

  await page.getByRole('button', { name: 'English' }).click()
  await expect(page.getByText('Notes that sync, without limits.')).toBeVisible()
})

test('a client-side route serves the app, and reloads', async ({ page }) => {
  await page.goto('/some/deep/link')
  await expectLoginPage(page)

  await page.reload()
  await expectLoginPage(page)
})

test('the page does not scroll sideways', async ({ page }) => {
  await page.goto('/login')
  await expectLoginPage(page)

  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  )
  expect(overflow).toBeLessThanOrEqual(0)
})

test('touch targets on a phone are at least 44 px', async ({ page, isMobile }) => {
  test.skip(!isMobile, 'Touch target sizes are for phones')
  await page.goto('/login')
  await expectLoginPage(page)

  const targets = [
    ...(await page.getByRole('button').all()),
    ...(await page.locator('input').all()),
  ]
  expect(targets.length).toBeGreaterThanOrEqual(4)
  for (const target of targets) {
    const box = await target.boundingBox()
    expect(box!.height).toBeGreaterThanOrEqual(44)
  }
})
