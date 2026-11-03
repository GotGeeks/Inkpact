import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { chooseLanguage, closeDrawer, createPageNamed, openSidebar, pageIdOf } from './helpers'

// Every project runs at the same moment on the same workspace, so each test searches for a word of its own.
const word = () => `zq${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`

const dialog = (page: Page) => page.getByTestId('search-dialog')
const input = (page: Page) => page.getByRole('combobox', { name: 'Search pages' })

/** The way a person gets there: the sidebar row on a wide screen, the button in the header on a phone. */
async function openSearch(page: Page) {
  const button = page.getByTestId('search-open')
  if (await button.isVisible()) await button.click()
  else await page.getByTestId('search-row').click()
  await expect(dialog(page)).toBeVisible()
  await expect(input(page)).toBeFocused()
}

async function makePage(page: Page, title: string) {
  await page.goto('/')
  const row = await createPageNamed(page, title)
  const id = await pageIdOf(row)
  await closeDrawer(page)
  return id
}

test('a page is found by its title while typing, marked, and opens with Enter', async ({
  page,
}) => {
  const token = word()
  const id = await makePage(page, `Alpha ${token} notes`)

  await openSearch(page)
  await input(page).pressSequentially(token)

  const result = page.locator(`[role="option"][data-page-id="${id}"]`)
  await expect(result).toBeVisible()
  await expect(result.locator('mark')).toHaveText(token)
  await expect(page.getByTestId('search-results').getByRole('option')).toHaveCount(1)

  await input(page).press('Enter')
  await expect(dialog(page)).toBeHidden()
  await expect(page).toHaveURL(new RegExp(`/p/${id}$`))
  await expect(page.getByTestId('page-title')).toHaveValue(`Alpha ${token} notes`)
})

test('case and accents do not matter, and half a word is enough', async ({ page }) => {
  const token = word()
  const id = await makePage(page, `Café ${token}`)

  await openSearch(page)
  await input(page).fill(`CAFE ${token.slice(0, 7)}`)

  await expect(page.locator(`[role="option"][data-page-id="${id}"]`)).toBeVisible()
})

test('Ctrl+K opens the search from inside the editor, and Escape closes it', async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'a keyboard shortcut')
  await makePage(page, `Typing ${word()}`)
  await openSidebar(page)
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await expect(editor).toHaveAttribute('contenteditable', 'true')
  await editor.click()
  await page.keyboard.type('some text')

  await page.keyboard.press('Control+k')

  await expect(dialog(page)).toBeVisible()
  await expect(input(page)).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(dialog(page)).toBeHidden()
  // Nothing was typed into the page by the shortcut.
  await expect(editor).toHaveText('some text')
})

test('the arrow keys choose a result, and the last search is there when it opens again', async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'a keyboard')
  const token = word()
  await makePage(page, `First ${token}`)
  await page.goto('/')
  await createPageNamed(page, `Second ${token}`)
  await closeDrawer(page)

  await openSearch(page)
  await input(page).fill(token)
  await expect(page.getByRole('option')).toHaveCount(2)
  await page.keyboard.press('ArrowDown')
  const second = page.getByRole('option').nth(1)
  await expect(second).toHaveAttribute('data-highlighted', '')
  await expect(input(page)).toHaveAttribute(
    'aria-activedescendant',
    (await second.getAttribute('id'))!,
  )
  const chosen = await page.getByRole('option').nth(1).getAttribute('data-page-id')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(`/p/${chosen}$`))

  await openSearch(page)
  await expect(input(page)).toHaveValue(token)
  await expect(page.getByRole('option')).toHaveCount(2)
})

test('it asks for more letters, and says when nothing is found', async ({ page }) => {
  await page.goto('/')
  await openSidebar(page)
  await closeDrawer(page)
  await openSearch(page)

  await input(page).fill('x')
  await expect(page.getByTestId('search-hint')).toHaveText('Type at least 2 letters to search.')

  await input(page).fill(`nothing${word()}`)
  await expect(page.getByTestId('search-empty')).toContainText('Nothing found for')
})

test('a page in the trash is not found, and comes back when it is restored', async ({ page }) => {
  const token = word()
  const id = await makePage(page, `Doomed ${token}`)
  const state = (await (await page.request.get('/api/auth/state')).json()) as { csrfToken: string }
  const headers = { 'X-CSRF-Token': state.csrfToken }

  await openSearch(page)
  await input(page).fill(token)
  await expect(page.locator(`[role="option"][data-page-id="${id}"]`)).toBeVisible()
  await page.keyboard.press('Escape')

  expect((await page.request.post(`/api/pages/${id}/trash`, { headers, data: {} })).ok()).toBe(true)
  await openSearch(page)
  // The last search is shown again, and found again.
  await expect(page.getByTestId('search-empty')).toBeVisible()
  await expect(page.locator(`[role="option"][data-page-id="${id}"]`)).toHaveCount(0)
  await page.keyboard.press('Escape')

  expect((await page.request.post(`/api/pages/${id}/restore`, { headers, data: {} })).ok()).toBe(
    true,
  )
  await openSearch(page)
  await expect(page.locator(`[role="option"][data-page-id="${id}"]`)).toBeVisible()
})

test('on a phone the dialog fills the screen, nothing runs off the side and the opened page shows', async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name === 'desktop', 'the phone viewports')
  const token = word()
  const id = await makePage(page, `Phone ${token}`)

  await openSearch(page)
  const box = (await dialog(page).boundingBox())!
  const viewport = page.viewportSize()!
  expect(box.width).toBeGreaterThanOrEqual(viewport.width - 1)
  expect(box.height).toBeGreaterThanOrEqual(viewport.height - 1)
  await input(page).fill(token)
  await expect(page.locator(`[role="option"][data-page-id="${id}"]`)).toBeVisible()
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth),
  ).toBeLessThanOrEqual(0)

  await page.locator(`[role="option"][data-page-id="${id}"]`).click()
  await expect(dialog(page)).toBeHidden()
  await expect(page).toHaveURL(new RegExp(`/p/${id}$`))
  await expect(page.getByTestId('page-title')).toHaveValue(`Phone ${token}`)
})

test('the search is in Dutch for someone who chose Dutch', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'the language is the same on every device')
  await page.goto('/')
  await chooseLanguage(page, 'Nederlands')

  await page.keyboard.press('Control+k')

  await expect(page.getByRole('combobox', { name: 'Pagina’s doorzoeken' })).toBeFocused()
  await expect(page.getByTestId('search-hint')).toHaveText('Typ minstens 2 letters om te zoeken.')
})
