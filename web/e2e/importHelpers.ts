import type { Page } from '@playwright/test'
import { expect } from './fixtures'
import { openUserMenu } from './helpers'
import type { GeneratedJex, LargeJex, LinkedJex } from './jex'

/** From the account menu, the way a person gets there. On a phone the menu is in the drawer, which closes behind it. */
export async function openImportPage(page: Page, menuItem = 'Import from Joplin') {
  await page.goto('/')
  await openUserMenu(page)
  await page.getByRole('menuitem', { name: menuItem, exact: true }).click()
  await expect(page).toHaveURL(/\/import\/joplin$/)
  await expect(page.getByTestId('import-choose')).toBeVisible()
}

/**
 * Picks the file and waits for the report. The devices of the suite share one workspace, and a workspace takes one
 * import at a time: when another device's import is running, the server says so and this tries again a moment later.
 */
export async function importJex(page: Page, jex: GeneratedJex | LinkedJex | LargeJex) {
  await expect(async () => {
    await page.getByTestId('import-file').setInputFiles({
      name: 'export.jex',
      mimeType: 'application/octet-stream',
      buffer: jex.file,
    })
    const result = page.getByTestId('import-result')
    const error = page.getByTestId('import-error')
    await expect(result.or(error)).toBeVisible({ timeout: 60_000 })
    await expect(error).toBeHidden({ timeout: 1_000 })
  }).toPass({ timeout: 180_000, intervals: [1_000, 2_000, 4_000] })
}
