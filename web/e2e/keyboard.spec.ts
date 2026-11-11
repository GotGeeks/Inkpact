import type { Locator, Page } from '@playwright/test'
import { ADMIN } from './credentials'
import { expect, test } from './fixtures'

/**
 * A session without the mouse, on the desktop: every step is reached with Tab, the arrow keys, Enter, Escape and the
 * shortcuts of the app, and each says where the focus is. A control that cannot be reached, or is reached without
 * showing where the focus is, fails here.
 */
test.beforeEach(({}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'a keyboard-only run belongs on the desktop')
})

const unique = (name: string) =>
  `${name} ${Date.now().toString(36)}${Math.random().toString(36).slice(2, 5)}`

const isFocused = (target: Locator) => target.evaluate((el) => el === document.activeElement)

/** Presses Tab until the target has the focus; fails if it is not reached within `max` presses. */
async function tabTo(page: Page, target: Locator, max = 60) {
  for (let i = 0; i < max; i++) {
    if (await isFocused(target)) return
    await page.keyboard.press('Tab')
  }
  expect(await isFocused(target), `Tab reached ${target}`).toBe(true)
}

/** The focus shows: an outline of at least 2 pixels, or a ring drawn as a shadow. */
async function expectFocusShown(target: Locator) {
  const shown = await target.evaluate((el) => {
    const style = getComputedStyle(el)
    return (
      (style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) >= 2) ||
      style.boxShadow !== 'none'
    )
  })
  const detail = await target.evaluate((el) => {
    const style = getComputedStyle(el)
    return `${el.tagName} ${style.outlineStyle} ${style.outlineWidth} ${el.matches(':focus-visible')} ${el.className}`
  })
  expect(shown, `the focus has an outline: ${detail}`).toBe(true)
}

test('signing in, and out again, with the keyboard alone', async ({ browser }) => {
  const context = await browser.newContext({ storageState: { cookies: [], origins: [] } })
  const page = await context.newPage()
  await page.goto('/login')

  const email = page.getByLabel('Email')
  const password = page.getByLabel('Password')
  await tabTo(page, email)
  await page.keyboard.type(ADMIN.email)
  await page.keyboard.press('Tab')
  await expect(password).toBeFocused()
  await page.keyboard.type(ADMIN.password)
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome', exact: true })).toBeVisible()

  // The account menu opens with Enter on its button, and its items are reached by typing their first letters.
  const account = page.getByRole('button', { name: /account menu$/ })
  await tabTo(page, account)
  await expectFocusShown(account)
  await page.keyboard.press('Enter')
  await expect(page.getByRole('menu')).toBeVisible()
  await page.keyboard.type('Sign')
  await expect(page.getByRole('menuitem', { name: 'Sign out', exact: true })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
  await context.close()
})

test('skip link, a new page, rename, move, search, typing, export, trash and restore', async ({
  page,
}) => {
  const title = unique('Walk')
  const renamed = unique('Renamed')
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome', exact: true })).toBeVisible()

  // 1. The first Tab is the skip link; it shows where the focus is and takes it to the page.
  await page.keyboard.press('Tab')
  const skip = page.getByTestId('skip-link')
  await expect(skip).toBeFocused()
  await expectFocusShown(skip)
  expect((await skip.boundingBox())!.width).toBeGreaterThan(40)
  await page.keyboard.press('Enter')
  await expect(page.locator('main')).toBeFocused()

  // 2. A new page: the button, the title field it gives the focus to, and Enter.
  const newPage = page.getByRole('button', { name: 'New page' }).first()
  await page.keyboard.press('Shift+Tab')
  await tabTo(page, newPage)
  await expectFocusShown(newPage)
  await page.keyboard.press('Enter')
  const field = page.getByRole('textbox', { name: 'Page title' })
  await expect(field).toBeFocused()
  await page.keyboard.type(title)
  await page.keyboard.press('Enter')

  // The tab knows which page is open.
  await expect(page).toHaveTitle(`${title} – Inkpact`)

  // 3. The row in the tree: renamed with F2, and the tab's title follows.
  const row = page.locator('[role="treeitem"]').filter({ hasText: title }).first()
  await tabTo(page, row)
  await expectFocusShown(row)
  await page.keyboard.press('F2')
  const rename = page.locator('[role="treeitem"] input[type="text"]')
  await expect(rename).toBeFocused()
  await page.keyboard.press('ControlOrMeta+a')
  await page.keyboard.type(renamed)
  await page.keyboard.press('Enter')
  await expect(page).toHaveTitle(`${renamed} – Inkpact`)

  // 4. The row's menu with Shift+F10, an item by typing, and the move dialog, closed with Escape.
  const renamedRow = page.locator('[role="treeitem"]').filter({ hasText: renamed }).first()
  await tabTo(page, renamedRow)
  await page.keyboard.press('Shift+F10')
  await expect(page.getByRole('menu')).toBeVisible()
  await page.keyboard.type('Move')
  await expect(page.getByRole('menuitem', { name: 'Move to…', exact: true })).toBeFocused()
  await page.keyboard.press('Enter')
  const moveDialog = page.getByRole('dialog', { name: /Move/ })
  await expect(moveDialog).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(moveDialog).toBeHidden()

  // 5. Search with the shortcut, from the start page (as after a reload): the field has the focus, Enter opens the page.
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome', exact: true })).toBeVisible()
  await page.keyboard.press('ControlOrMeta+k')
  const search = page.getByRole('combobox', { name: 'Search pages' })
  await expect(search).toBeFocused()
  await page.keyboard.type(renamed.split(' ')[1]!)
  await expect(page.getByTestId('search-results').getByRole('option').first()).toBeVisible()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('search-dialog')).toBeHidden()
  await expect(page).toHaveURL(/\/p\//)
  // A screen reader hears which page opened, and the focus stayed where it was.
  await expect(page.getByTestId('announcer')).toHaveText(`Opened: ${renamed}`)

  // 6. Typing in the page: to the title field and, with Enter, into the text.
  const titleField = page.getByTestId('page-title')
  await tabTo(page, titleField)
  await expectFocusShown(titleField)
  await page.keyboard.press('Enter')
  const body = page.getByTestId('editor').locator('.ProseMirror')
  await expect(body).toBeFocused()
  await page.keyboard.type('Written without a mouse')
  await expect(body).toContainText('Written without a mouse')

  // 7. The export from the account menu starts a download.
  const account = page.getByRole('button', { name: /account menu$/ })
  await tabTo(page, account)
  await page.keyboard.press('Enter')
  await expect(page.getByRole('menu')).toBeVisible()
  await page.keyboard.type('Export')
  await expect(page.getByTestId('export-item')).toBeFocused()
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.keyboard.press('Enter'),
  ])
  expect(download.suggestedFilename()).toMatch(/\.zip$/)

  // 8. Delete puts the row in the trash, the trash link opens the list, and Restore brings the page back.
  await tabTo(page, renamedRow)
  await page.keyboard.press('Delete')
  await expect(page.locator('[role="treeitem"]').filter({ hasText: renamed })).toHaveCount(0)
  const trash = page.getByRole('link', { name: /Trash/ })
  await tabTo(page, trash)
  await expectFocusShown(trash)
  await page.keyboard.press('Enter')
  await expect(page).toHaveTitle('Trash – Inkpact')
  const restore = page.getByRole('button', { name: `Restore ${renamed}` })
  await tabTo(page, restore)
  await expectFocusShown(restore)
  await page.keyboard.press('Enter')
  await expect(page.locator('[role="treeitem"]').filter({ hasText: renamed })).toHaveCount(1)
})

test('the palette with the keyboard alone: its tabs, a command, the shortcut help, and the focus back where it was', async ({
  page,
}) => {
  await page.goto('/')
  const opener = page.getByTestId('search-row')
  await expect(opener).toBeVisible()
  await tabTo(page, opener)
  await page.keyboard.press('Enter')
  const dialog = page.getByTestId('search-dialog')
  await expect(page.getByRole('combobox', { name: 'Search pages' })).toBeFocused()

  // Back from the field to the tabs, which the arrow keys move between.
  const pagesTab = page.getByRole('tab', { name: 'Pages' })
  for (let i = 0; i < 5 && !(await isFocused(pagesTab)); i++) await page.keyboard.press('Shift+Tab')
  await expect(pagesTab).toBeFocused()
  await expectFocusShown(pagesTab)
  await page.keyboard.press('ArrowRight')
  await expect(page.getByRole('tab', { name: 'Tags' })).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('ArrowRight')
  const commandsTab = page.getByRole('tab', { name: 'Commands' })
  await expect(commandsTab).toHaveAttribute('aria-selected', 'true')

  const commands = page.getByRole('combobox', { name: 'Search commands' })
  await tabTo(page, commands, 5)
  await page.keyboard.type('shortcut')
  await expect(page.getByRole('option')).toHaveCount(1)
  await page.keyboard.press('Enter')
  const back = page.getByRole('button', { name: 'Back to the palette' })
  await expect(back).toBeFocused()
  await expect(dialog).toContainText('Ctrl+Alt+N')
  await page.keyboard.press('Enter')
  await expect(commands).toBeFocused()

  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(opener).toBeFocused()
})
