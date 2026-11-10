import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { closeDrawer, createPageNamed, pageIdOf } from './helpers'

const unique = (project: string) => `palette-${project}-${Date.now().toString(36)}`

async function openPalette(page: Page) {
  const searchButton = page.getByTestId('search-open')
  if (await searchButton.isVisible()) await searchButton.click()
  else await page.getByTestId('search-row').click()
  await expect(page.getByTestId('search-dialog')).toBeVisible()
}

test('pages, tags and commands work in the quick switcher', async ({ page }, testInfo) => {
  const title = unique(testInfo.project.name)
  const tagName = `${title}-tag`
  await page.goto('/')
  const row = await createPageNamed(page, title)
  const pageId = await pageIdOf(row)
  await row.click()
  await closeDrawer(page)
  await expect(page.getByTestId('page-title')).toHaveValue(title)

  await page.getByRole('button', { name: 'Favorite', exact: true }).click()
  await page.getByTestId('add-tag-button').click()
  await page.getByTestId('add-tag-field').fill(tagName)
  await page.getByRole('option', { name: `Create tag “${tagName}”` }).click()

  await openPalette(page)
  await expect(page.getByRole('combobox', { name: 'Search pages' })).toBeFocused()
  const favorite = page.locator(`[role="option"][data-page-id="${pageId}"]`)
  await expect(favorite).toHaveCount(1)

  await page.getByRole('tab', { name: 'Tags' }).click()
  await page.getByRole('combobox', { name: 'Search tags' }).fill(tagName)
  const tag = page.getByRole('option', { name: tagName })
  await expect(tag).toBeVisible()
  await tag.click()
  await expect(page).toHaveURL(/\/tags\/[^/]+$/)
  await expect(page.getByLabel('Tag name')).toHaveValue(tagName)

  await openPalette(page)
  await page.getByRole('tab', { name: 'Commands' }).click()
  const createCommand = page.getByRole('option', { name: /Create new page/ })
  await expect(createCommand).toBeVisible()
  await createCommand.click()
  await expect(page).toHaveURL(/\/p\/[^/]+$/)
  await expect(page.getByTestId('page-title')).toBeFocused()
})

test('the command and new-page shortcuts open their intended actions', async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop')
  await page.goto('/')
  await expect(page.getByTestId('user-name')).toBeVisible()

  await page.keyboard.press('Control+Alt+k')
  await expect(page.getByTestId('search-dialog')).toBeVisible()
  await expect(page.locator('[role="tab"][aria-selected="true"]')).toHaveText('Commands')
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('search-dialog')).toBeHidden()

  await page.keyboard.press('Control+Alt+n')
  await expect(page.getByTestId('page-title')).toBeFocused()
})
