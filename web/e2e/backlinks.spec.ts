import type { Locator, Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { closeDrawer, openSidebar, rowFor } from './helpers'
import { importJex, openImportPage } from './importHelpers'
import { generateLinkedJex } from './jex'

const editorBody = (page: Page) => page.getByTestId('editor').locator('.ProseMirror')

const unique = (project: string) => `${project}-${Date.now().toString(36)}`

/** Picks an action from the menu of a row. */
async function chooseAction(page: Page, row: Locator, action: string) {
  await row.getByRole('button', { name: /^Actions for / }).click()
  await page.getByRole('menuitem', { name: action, exact: true }).click()
}

/**
 * Reads the page again until the list of pages that link to it says `expected`: the server indexes a link when the page
 * is compacted, which the e2e server does a few seconds after the last edit (see playwright.config.ts).
 */
async function expectBacklinks(page: Page, expected: string) {
  await expect(async () => {
    await page.reload()
    await expect(page.getByTestId('backlinks')).toContainText(expected, { timeout: 4_000 })
  }).toPass({ timeout: 90_000, intervals: [2_000] })
}

test('the pages that link to a page are listed under it, and a link to a page in the trash is struck through until it is back', async ({
  page,
}, testInfo) => {
  test.setTimeout(240_000)
  const jex = generateLinkedJex(unique(testInfo.project.name))
  await openImportPage(page)
  await importJex(page, jex)

  // "Start here" and "The destination" link to each other.
  await openSidebar(page)
  await rowFor(page, jex.titles.notebook)
    .getByRole('button', { name: `Expand ${jex.titles.notebook}` })
    .click()
  await rowFor(page, jex.titles.first).click()
  await closeDrawer(page)
  await expect(editorBody(page)).toContainText('Start of the trail.')
  await expectBacklinks(page, jex.titles.second)
  await expect(page.getByTestId('backlinks').getByRole('heading')).toHaveText('Linked from 1 page')

  // The list opens the page that links here, which links back.
  await page.getByTestId('backlinks').getByRole('link', { name: jex.titles.second }).click()
  await expect(page.getByTestId('page-title')).toHaveValue(jex.titles.second)
  await expect(editorBody(page)).toContainText('Destination page text.')
  await expectBacklinks(page, jex.titles.first)
  const back = editorBody(page).getByRole('link', { name: /Back to the start/ })
  await expect(back).toBeVisible()
  await expect(editorBody(page).locator('[data-broken-link]')).toHaveCount(0)

  // The page it links to goes to the trash: the link stays, struck through, and the page no longer lists it as a backlink.
  await openSidebar(page)
  await chooseAction(page, rowFor(page, jex.titles.first), 'Move to trash')
  // On a phone the drawer makes way for the Undo notice by itself. Not with Escape (closeDrawer): every notice closes on
  // Escape, and the key would take the Undo away when the notice is already there.
  await expect(page.getByRole('dialog')).toBeHidden()
  await expect(page.getByRole('button', { name: 'Undo', exact: true })).toBeVisible()
  const broken = editorBody(page).locator('[data-broken-link="trash"]')
  await expect(broken).toContainText('Back to the start')
  await expect(broken).toHaveCSS('text-decoration-line', 'line-through')
  await expect(editorBody(page).locator('.sr-only')).toContainText('This page is in the trash')
  await expect(page.getByTestId('backlinks')).toHaveCount(0)

  // Back from the trash: the link is whole again, and nothing was written into the page for it.
  await page.getByRole('button', { name: 'Undo', exact: true }).click()
  await expect(editorBody(page).locator('[data-broken-link]')).toHaveCount(0)
  await expect(page.getByTestId('backlinks')).toContainText(jex.titles.first)
})
