import type { Locator, Page, TestInfo } from '@playwright/test'
import { expect, test } from './fixtures'
import { closeDrawer, createPageNamed, openSidebar, pageIdOf } from './helpers'

// Every project runs these at the same moment in the same workspace, so each test works with pages of its own, found by a
// title no other test uses.
const suffix = (testInfo: TestInfo) => `${testInfo.project.name}-${Date.now().toString(36)}`

/** Picks an action from the menu of a row. */
async function chooseAction(page: Page, row: Locator, action: string) {
  await row.getByRole('button', { name: /^Actions for / }).click()
  await page.getByRole('menuitem', { name: action, exact: true }).click()
}

const favoritesNav = (page: Page) => page.getByRole('navigation', { name: 'Favorites' })

async function workspaceId(page: Page) {
  const workspaces = (await (await page.request.get('/api/workspaces')).json()) as { id: string }[]
  return workspaces[0]!.id
}

/** What the server lists, read the way the app reads it. */
async function listed(page: Page, what: 'favorites' | 'recent'): Promise<string[]> {
  const answer = (await (
    await page.request.get(`/api/workspaces/${await workspaceId(page)}/${what}`)
  ).json()) as (string | { pageId: string })[]
  return answer.map((item) => (typeof item === 'string' ? item : item.pageId))
}

test('a page is starred from its header, is at the top of the sidebar after a reload, and is unstarred from its row menu', async ({
  page,
}, testInfo) => {
  const title = `Starred-${suffix(testInfo)}`
  await page.goto('/')
  const row = await createPageNamed(page, title)
  const id = await pageIdOf(row)
  await closeDrawer(page)

  const star = page.getByRole('button', { name: 'Favorite', exact: true })
  await expect(star).toHaveAttribute('aria-pressed', 'false')
  await star.click()
  await expect(star).toHaveAttribute('aria-pressed', 'true')

  await openSidebar(page)
  await expect(favoritesNav(page).getByRole('link', { name: title })).toBeVisible()

  // The server has it, so it is there after a reload (and on another device).
  await expect.poll(async () => (await listed(page, 'favorites')).includes(id)).toBe(true)
  await page.reload()
  await expect(page.getByRole('button', { name: 'Favorite', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  )
  await openSidebar(page)
  await expect(favoritesNav(page).getByRole('link', { name: title })).toBeVisible()
  expect(await listed(page, 'favorites')).toContain(id)

  // Taken away from the row menu.
  await chooseAction(
    page,
    page.locator(`[role="treeitem"][data-page-id="${id}"]`),
    'Remove from favorites',
  )
  await expect(favoritesNav(page).getByRole('link', { name: title })).toHaveCount(0)
  expect(await listed(page, 'favorites')).not.toContain(id)
})

test('a page is starred from its row menu with the keyboard', async ({ page }, testInfo) => {
  const title = `Keys-${suffix(testInfo)}`
  await page.goto('/')
  const row = await createPageNamed(page, title)
  await row.focus()
  await page.keyboard.press('ContextMenu')
  await page.getByRole('menuitem', { name: 'Add to favorites', exact: true }).click()

  await expect(favoritesNav(page).getByRole('link', { name: title })).toBeVisible()
})

test('the pages opened last are on the start page, the last one first, and leave it with the trash', async ({
  page,
}, testInfo) => {
  const first = `First-${suffix(testInfo)}`
  const second = `Second-${suffix(testInfo)}`
  await page.goto('/')
  await createPageNamed(page, first)
  const secondRow = await createPageNamed(page, second)
  const secondId = await pageIdOf(secondRow)
  await closeDrawer(page)
  await expect(page.getByTestId('page-title')).toHaveValue(second)

  // Each open is recorded when the page opens; wait for the server to have both.
  await expect.poll(async () => (await listed(page, 'recent')).includes(secondId)).toBe(true)

  await page.goto('/')
  const recent = page.getByTestId('recent-pages')
  await expect(recent.getByRole('heading', { name: 'Recently opened' })).toBeVisible()
  const lines = await recent.getByRole('listitem').allTextContents()
  const at = (title: string) => lines.findIndex((line) => line.includes(title))
  expect(at(second)).toBeGreaterThanOrEqual(0)
  expect(at(first)).toBeGreaterThan(at(second))

  // In the trash it is in neither list; Undo brings it back to both.
  await page.request.put(`/api/pages/${secondId}/favorite`, {
    headers: {
      'X-CSRF-Token': (
        (await (await page.request.get('/api/auth/state')).json()) as { csrfToken: string }
      ).csrfToken,
    },
    data: {},
  })
  await page.reload()
  await openSidebar(page)
  await expect(favoritesNav(page).getByRole('link', { name: second })).toBeVisible()

  await chooseAction(
    page,
    page.locator(`[role="treeitem"][data-page-id="${secondId}"]`),
    'Move to trash',
  )
  await expect.poll(async () => await listed(page, 'recent')).not.toContain(secondId)
  await expect.poll(async () => await listed(page, 'favorites')).not.toContain(secondId)

  await page
    .getByRole('region', { name: /Notifications/ })
    .getByRole('button', { name: 'Undo' })
    .click()
  await expect.poll(async () => await listed(page, 'recent')).toContain(secondId)
  expect(await listed(page, 'favorites')).toContain(secondId)

  await page.goto('/')
  await expect(page.getByTestId('recent-pages').getByRole('link', { name: second })).toBeVisible()
})
