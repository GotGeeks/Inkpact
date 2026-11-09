import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { openUserMenu } from './helpers'

/** The colour of the canvas (the body), as the browser computed it. */
const canvas = (page: Page) => page.evaluate(() => getComputedStyle(document.body).backgroundColor)

const WHITE = 'rgb(255, 255, 255)'
const DARK = 'rgb(9, 10, 15)'

async function choose(page: Page, theme: 'System' | 'Light' | 'Dark') {
  await openUserMenu(page)
  await page.getByRole('menuitemradio', { name: theme, exact: true }).click()
  // On a phone the drawer stays open behind the menu.
  if (await page.getByRole('dialog').isVisible()) {
    await page.keyboard.press('Escape')
    await expect(page.getByRole('dialog')).toBeHidden()
  }
}

test('the app follows the system: light when it is light, dark when it is dark', async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: 'light' })
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome', exact: true })).toBeVisible()
  expect(await canvas(page)).toBe(WHITE)
  await expect(page.locator('html')).not.toHaveAttribute('data-theme', /.+/)

  await page.emulateMedia({ colorScheme: 'dark' })
  expect(await canvas(page)).toBe(DARK)
})

test('a theme chosen in the menu wins over the system and stays after a reload; System follows the system again', async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: 'dark' })
  await page.goto('/')
  expect(await canvas(page)).toBe(DARK)

  await choose(page, 'Light')
  expect(await canvas(page)).toBe(WHITE)
  // The browser's bar follows the choice: the light colour applies whatever the system says.
  await expect(page.locator('meta[name="theme-color"][data-scheme="light"]')).toHaveAttribute(
    'media',
    'all',
  )
  await expect(page.locator('meta[name="theme-color"][data-scheme="dark"]')).toHaveAttribute(
    'media',
    'not all',
  )
  await page.reload()
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome', exact: true })).toBeVisible()
  expect(await canvas(page)).toBe(WHITE)

  await choose(page, 'System')
  expect(await canvas(page)).toBe(DARK)
  await page.reload()
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome', exact: true })).toBeVisible()
  expect(await canvas(page)).toBe(DARK)

  // Dark against a light system.
  await page.emulateMedia({ colorScheme: 'light' })
  await choose(page, 'Dark')
  expect(await canvas(page)).toBe(DARK)
})
