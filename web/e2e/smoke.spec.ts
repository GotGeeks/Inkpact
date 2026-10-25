import { expect, test } from './fixtures'

test('the app shell renders in English', async ({ page }) => {
  await page.goto('/')

  await expect(page).toHaveTitle('Inkpact')
  await expect(page.getByRole('heading', { name: 'Inkpact', level: 1 })).toBeVisible()
  await expect(page.getByText('Notes that sync, without limits.')).toBeVisible()
})

test('the language switch changes the text, with the keyboard', async ({ page }) => {
  await page.goto('/')

  const dutch = page.getByRole('button', { name: 'Nederlands' })
  await dutch.focus()
  await page.keyboard.press('Space')

  await expect(page.getByText('Notities die synchroniseren, zonder limieten.')).toBeVisible()
  await expect(page.getByRole('group', { name: 'Taal' })).toBeVisible()
})

test('the language switch changes the text, by pointer or touch', async ({ page }) => {
  await page.goto('/')

  await page.getByRole('button', { name: 'Nederlands' }).click()
  await expect(page.getByText('Notities die synchroniseren, zonder limieten.')).toBeVisible()

  await page.getByRole('button', { name: 'English' }).click()
  await expect(page.getByText('Notes that sync, without limits.')).toBeVisible()
})

test('a client-side route serves the app, and reloads', async ({ page }) => {
  await page.goto('/some/deep/link')
  await expect(page.getByRole('heading', { name: 'Inkpact', level: 1 })).toBeVisible()

  await page.reload()
  await expect(page.getByRole('heading', { name: 'Inkpact', level: 1 })).toBeVisible()
})

test('the page does not scroll sideways', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Inkpact', level: 1 })).toBeVisible()

  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  )
  expect(overflow).toBeLessThanOrEqual(0)
})

test('touch targets on a phone are at least 44 px', async ({ page, isMobile }) => {
  test.skip(!isMobile, 'Touch target sizes are for phones')
  await page.goto('/')

  for (const radio of await page.getByRole('button').all()) {
    const box = await radio.boundingBox()
    expect(box!.height).toBeGreaterThanOrEqual(44)
  }
})
