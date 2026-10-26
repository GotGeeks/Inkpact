import type { Locator, Page, TestInfo } from '@playwright/test'
import { expect, test } from './fixtures'
import { createPageNamed, openSidebar, pageIdOf, rowFor } from './helpers'

// Every project runs these at the same moment in the same workspace, so each test works with pages of its own, found
// by a title no other test uses. What the others do around them is none of its business.
const suffix = (testInfo: TestInfo) => `${testInfo.project.name}-${Date.now().toString(36)}`

/** Picks an action from the menu of a row. */
async function chooseAction(page: Page, row: Locator, action: string) {
  await row.getByRole('button', { name: /^Actions for / }).click()
  await page.getByRole('menuitem', { name: action, exact: true }).click()
}

/** Where the lines for these titles are in the sidebar, top to bottom. */
async function positionsOf(page: Page, titles: string[]) {
  const lines = await page
    .locator('[role="treeitem"]')
    .evaluateAll((items) => items.map((item) => item.textContent ?? ''))
  return titles.map((title) => lines.findIndex((line) => line.includes(title)))
}

/** Opens the trash page from the sidebar (on a phone the drawer closes after a page is trashed). */
async function openTrash(page: Page) {
  await openSidebar(page)
  await page.getByRole('link', { name: /Trash/ }).click()
}

/** A notice in the toast area. The screen-reader copy of it is left out. */
function notice(page: Page, text: string) {
  return page.getByRole('region', { name: /Notifications/ }).getByText(text, { exact: true })
}

async function levelOf(row: Locator) {
  return Number(await row.getAttribute('aria-level'))
}

test.describe('the page tree', () => {
  test('a new page is named in the sidebar, opened, and shows its title', async ({
    page,
  }, testInfo) => {
    const title = `Fresh-${suffix(testInfo)}`
    await page.goto('/')

    const row = await createPageNamed(page, title)
    await row.click()

    await expect(page).toHaveURL(/\/p\/[0-9a-f-]{36}$/)
    await expect(page.getByRole('heading', { level: 1, name: title, exact: true })).toBeVisible()
    await openSidebar(page)
    await expect(rowFor(page, title)).toHaveAttribute('aria-selected', 'true')
  })

  test('the sidebar is a labelled tree, and its lines say how deep they are', async ({
    page,
  }, testInfo) => {
    const parent = `Tree-${suffix(testInfo)}`
    await page.goto('/')
    const row = await createPageNamed(page, parent)

    await expect(page.getByRole('tree', { name: 'Pages' })).toBeVisible()
    await expect(row).toHaveAttribute('aria-level', '1')
    await expect(row).toHaveAttribute('aria-selected', /true|false/)
  })

  test('a subpage is added from the menu, shows below its parent, and the open branch is remembered', async ({
    page,
  }, testInfo) => {
    const id = suffix(testInfo)
    const parent = `Parent-${id}`
    const child = `Child-${id}`
    await page.goto('/')
    const parentRow = await createPageNamed(page, parent)

    await chooseAction(page, parentRow, 'Add subpage')
    const field = page.getByRole('textbox', { name: 'Page title' })
    await field.fill(child)
    await field.press('Enter')

    const childRow = rowFor(page, child)
    await expect(childRow).toBeVisible()
    await expect(childRow).toHaveAttribute('aria-level', '2')
    await expect(rowFor(page, parent)).toHaveAttribute('aria-expanded', 'true')
    expect((await positionsOf(page, [parent, child]))[0]).toBeLessThan(
      (await positionsOf(page, [parent, child]))[1]!,
    )

    // Closing the branch hides the child; a reload shows the branch as it was left.
    await rowFor(page, parent)
      .getByRole('button', { name: /^Collapse / })
      .click()
    await expect(childRow).toBeHidden()
    await rowFor(page, parent)
      .getByRole('button', { name: /^Expand / })
      .click()
    await expect(childRow).toBeVisible()
    await page.reload()
    await openSidebar(page)
    await expect(rowFor(page, child)).toBeVisible()
  })

  test('a page is renamed from the menu', async ({ page }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const row = await createPageNamed(page, `Before-${id}`)

    await chooseAction(page, row, 'Rename')
    const field = page.getByRole('textbox', { name: 'Page title' })
    await expect(field).toHaveValue(`Before-${id}`)
    await field.fill(`After-${id}`)
    await field.press('Enter')

    await expect(rowFor(page, `After-${id}`)).toBeVisible()
    await expect(rowFor(page, `Before-${id}`)).toHaveCount(0)
  })

  test('Escape leaves the old title', async ({ page }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const row = await createPageNamed(page, `Keep-${id}`)

    await chooseAction(page, row, 'Rename')
    const field = page.getByRole('textbox', { name: 'Page title' })
    await field.fill('Something else')
    await field.press('Escape')

    await expect(rowFor(page, `Keep-${id}`)).toBeVisible()
    await expect(page.getByRole('textbox', { name: 'Page title' })).toHaveCount(0)
  })

  test('a page is moved under another with Move to…', async ({ page }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const mover = await createPageNamed(page, `Mover-${id}`)
    await createPageNamed(page, `Target-${id}`)

    await chooseAction(page, mover, 'Move to…')
    const dialog = page.getByRole('dialog', { name: `Move “Mover-${id}” to` })
    await expect(dialog).toBeVisible()
    await dialog.getByRole('button', { name: `Target-${id}`, exact: true }).click()

    await expect(dialog).toBeHidden()
    const moved = rowFor(page, `Mover-${id}`)
    await expect(moved).toHaveAttribute('aria-level', '2')
    await expect(rowFor(page, `Target-${id}`)).toHaveAttribute('aria-expanded', 'true')

    // And back to the top level.
    await chooseAction(page, moved, 'Move to…')
    await page.getByRole('dialog').getByRole('button', { name: 'Top level' }).click()
    await expect(rowFor(page, `Mover-${id}`)).toHaveAttribute('aria-level', '1')
  })

  test('the Move to dialog does not offer the page itself or what is below it', async ({
    page,
  }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const parent = await createPageNamed(page, `Top-${id}`)
    await chooseAction(page, parent, 'Add subpage')
    await page.getByRole('textbox', { name: 'Page title' }).fill(`Below-${id}`)
    await page.keyboard.press('Enter')
    await expect(rowFor(page, `Below-${id}`)).toBeVisible()

    await chooseAction(page, rowFor(page, `Top-${id}`), 'Move to…')

    const dialog = page.getByRole('dialog', { name: /^Move “Top-/ })
    await expect(dialog.getByRole('button', { name: 'Top level' })).toBeVisible()
    await expect(dialog.getByRole('button', { name: `Top-${id}`, exact: true })).toHaveCount(0)
    await expect(dialog.getByRole('button', { name: `Below-${id}`, exact: true })).toHaveCount(0)
    await dialog.getByRole('button', { name: 'Cancel' }).click()
    await expect(dialog).toBeHidden()
  })
})

test.describe('with the keyboard', () => {
  test.skip(({ isMobile }) => isMobile, 'A phone has no keyboard to try this with')

  test('arrow keys move through the tree, F2 renames, and Alt+arrows move a page', async ({
    page,
  }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    // The pages sit under one of their own, so that what other tests do at the top level cannot come between them.
    const keys = await createPageNamed(page, `Keys-${id}`)
    for (const title of ['First', 'Second', 'Third']) {
      await chooseAction(page, rowFor(page, `Keys-${id}`), 'Add subpage')
      const field = page.getByRole('textbox', { name: 'Page title' })
      await field.fill(`${title}-${id}`)
      await field.press('Enter')
      await expect(rowFor(page, `${title}-${id}`)).toBeVisible()
    }
    await expect(keys).toHaveAttribute('aria-expanded', 'true')
    const first = rowFor(page, `First-${id}`)
    const second = rowFor(page, `Second-${id}`)
    const third = rowFor(page, `Third-${id}`)

    // Arrow keys: focus goes to the line below and back.
    await first.focus()
    await page.keyboard.press('ArrowDown')
    await expect(page.locator('[role="treeitem"]:focus')).toHaveCount(1)
    await page.keyboard.press('ArrowUp')
    await expect(first).toBeFocused()

    // Alt+Down moves a page below the next one at its level; Alt+Up moves it back.
    const firstIsAboveSecond = async () => {
      const [a, b] = await positionsOf(page, [`First-${id}`, `Second-${id}`])
      return a! < b!
    }
    await first.focus()
    await page.keyboard.press('Alt+ArrowDown')
    await expect.poll(firstIsAboveSecond).toBe(false)
    await expect(rowFor(page, `First-${id}`)).toBeFocused()
    await page.keyboard.press('Alt+ArrowUp')
    await expect.poll(firstIsAboveSecond).toBe(true)

    // Alt+Right nests a page under the one above it; Alt+Left brings it back out.
    await third.focus()
    await page.keyboard.press('Alt+ArrowRight')
    await expect(rowFor(page, `Third-${id}`)).toHaveAttribute('aria-level', '3')
    await page.keyboard.press('Alt+ArrowLeft')
    await expect(rowFor(page, `Third-${id}`)).toHaveAttribute('aria-level', '2')

    // Left and Right close and open a branch.
    await chooseAction(page, second, 'Add subpage')
    const name = page.getByRole('textbox', { name: 'Page title' })
    await name.fill(`Leaf-${id}`)
    await name.press('Enter')
    await expect(rowFor(page, `Leaf-${id}`)).toBeVisible()
    await second.focus()
    await page.keyboard.press('ArrowLeft')
    await expect(second).toHaveAttribute('aria-expanded', 'false')
    await expect(rowFor(page, `Leaf-${id}`)).toBeHidden()
    await page.keyboard.press('ArrowRight')
    await expect(rowFor(page, `Leaf-${id}`)).toBeVisible()

    // F2 renames the page that has the focus.
    await second.focus()
    await page.keyboard.press('F2')
    const field = page.getByRole('textbox', { name: 'Page title' })
    await expect(field).toBeFocused()
    await field.fill(`Renamed-${id}`)
    await field.press('Enter')
    await expect(rowFor(page, `Renamed-${id}`)).toBeFocused()
  })

  test('Delete moves the page to the trash and Undo brings it back', async ({ page }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const row = await createPageNamed(page, `Doomed-${id}`)

    await row.focus()
    await page.keyboard.press('Delete')

    await expect(rowFor(page, `Doomed-${id}`)).toHaveCount(0)
    await expect(notice(page, `“Doomed-${id}” was moved to the trash.`)).toBeVisible()
    await page.getByRole('button', { name: 'Undo', exact: true }).click()
    await expect(rowFor(page, `Doomed-${id}`)).toBeVisible()
  })

  test('only one line can be reached with Tab', async ({ page }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    await createPageNamed(page, `Tab-${id}`)

    await expect(page.locator('[role="treeitem"][tabindex="0"]')).toHaveCount(1)
  })

  test('the context menu key opens the menu of the page', async ({ page }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const row = await createPageNamed(page, `Menu-${id}`)

    await row.focus()
    await page.keyboard.press('ContextMenu')

    await expect(page.getByRole('menuitem', { name: 'Rename' })).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(page.getByRole('menuitem', { name: 'Rename' })).toBeHidden()
  })
})

test.describe('with a mouse', () => {
  test.skip(({ isMobile }) => isMobile, 'A finger scrolls; on a phone pages move with Move to…')

  test('a page is dragged onto another to go under it, and above one to go before it', async ({
    page,
  }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    await createPageNamed(page, `Anchor-${id}`)
    await createPageNamed(page, `Dragged-${id}`)
    await createPageNamed(page, `Other-${id}`)
    const dragged = () => rowFor(page, `Dragged-${id}`)

    // The middle of a row: under it.
    await dragged().dragTo(rowFor(page, `Anchor-${id}`))
    await expect(dragged()).toHaveAttribute('aria-level', '2')
    await expect(rowFor(page, `Anchor-${id}`)).toHaveAttribute('aria-expanded', 'true')

    // The top quarter of a row: just before it, at the same level.
    // Other tests change the list around this one, which can move the target between the press and the drop; a drag
    // that missed is simply tried again.
    await expect(async () => {
      await dragged().dragTo(rowFor(page, `Other-${id}`), { targetPosition: { x: 40, y: 2 } })
      await expect(dragged()).toHaveAttribute('aria-level', '1', { timeout: 1500 })
    }).toPass()
    const [moved, other] = await positionsOf(page, [`Dragged-${id}`, `Other-${id}`])
    expect(moved).toBeLessThan(other!)
    expect(other! - moved!).toBe(1)
  })

  test('a page cannot be dropped on something below it', async ({ page }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const parent = await createPageNamed(page, `Branch-${id}`)
    await chooseAction(page, parent, 'Add subpage')
    await page.getByRole('textbox', { name: 'Page title' }).fill(`Leaf-${id}`)
    await page.keyboard.press('Enter')
    await expect(rowFor(page, `Leaf-${id}`)).toBeVisible()

    await rowFor(page, `Branch-${id}`).dragTo(rowFor(page, `Leaf-${id}`))

    await expect(rowFor(page, `Branch-${id}`)).toHaveAttribute('aria-level', '1')
    await expect(rowFor(page, `Leaf-${id}`)).toHaveAttribute('aria-level', '2')
  })

  test('rows are draggable by mouse only for people who may edit', async ({ page }, testInfo) => {
    await page.goto('/')
    const row = await createPageNamed(page, `Drag-${suffix(testInfo)}`)

    await expect(row).toHaveAttribute('draggable', 'true')
  })
})

test.describe('the trash', () => {
  test('a page goes to the trash and is restored from the trash page', async ({
    page,
  }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const row = await createPageNamed(page, `Trashed-${id}`)

    await chooseAction(page, row, 'Move to trash')
    await expect(rowFor(page, `Trashed-${id}`)).toHaveCount(0)
    await expect(notice(page, `“Trashed-${id}” was moved to the trash.`)).toBeVisible()

    await openTrash(page)
    await expect(page.getByRole('heading', { level: 1, name: 'Trash', exact: true })).toBeVisible()
    await expect(page.getByTestId('trash-list')).toContainText(`Trashed-${id}`)

    await page.getByRole('button', { name: `Restore Trashed-${id}` }).click()
    await expect(page.getByTestId('trash-list').getByText(`Trashed-${id}`)).toHaveCount(0)
    await expect(notice(page, `“Trashed-${id}” was restored.`)).toBeVisible()

    await page.goto('/')
    await openSidebar(page)
    await expect(rowFor(page, `Trashed-${id}`)).toBeVisible()
  })

  test('Undo in the notice brings the page back at once', async ({ page }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const row = await createPageNamed(page, `Undone-${id}`)

    await chooseAction(page, row, 'Move to trash')
    await page.getByRole('button', { name: 'Undo', exact: true }).click()

    await expect(notice(page, `“Undone-${id}” was restored.`)).toBeVisible()
    await openSidebar(page)
    await expect(rowFor(page, `Undone-${id}`)).toBeVisible()
  })

  test('trashing a parent takes its children along, and restoring it brings them back', async ({
    page,
  }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const parent = await createPageNamed(page, `Folder-${id}`)
    await chooseAction(page, parent, 'Add subpage')
    await page.getByRole('textbox', { name: 'Page title' }).fill(`Inside-${id}`)
    await page.keyboard.press('Enter')
    await expect(rowFor(page, `Inside-${id}`)).toBeVisible()

    await chooseAction(page, rowFor(page, `Folder-${id}`), 'Move to trash')
    await expect(notice(page, `“Folder-${id}” was moved to the trash.`)).toBeVisible()
    await openSidebar(page)
    await expect(rowFor(page, `Folder-${id}`)).toHaveCount(0)
    await expect(rowFor(page, `Inside-${id}`)).toHaveCount(0)

    await openTrash(page)
    // The trash lists the top of what was trashed, which is what a person restores.
    await expect(page.getByTestId('trash-list')).toContainText(`Folder-${id}`)
    await expect(page.getByTestId('trash-list')).not.toContainText(`Inside-${id}`)
    await page.getByRole('button', { name: `Restore Folder-${id}` }).click()
    await expect(notice(page, `“Folder-${id}” was restored.`)).toBeVisible()

    await page.goto('/')
    await openSidebar(page)
    await expect(rowFor(page, `Folder-${id}`)).toBeVisible()
    await expect(rowFor(page, `Inside-${id}`)).toBeVisible()
  })

  test('a child trashed on its own stays in the trash when its parent comes back', async ({
    page,
  }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const parent = await createPageNamed(page, `Home-${id}`)
    await chooseAction(page, parent, 'Add subpage')
    await page.getByRole('textbox', { name: 'Page title' }).fill(`Kid-${id}`)
    await page.keyboard.press('Enter')
    await chooseAction(page, rowFor(page, `Kid-${id}`), 'Move to trash')
    await expect(notice(page, `“Kid-${id}” was moved to the trash.`)).toBeVisible()
    await openSidebar(page)
    await expect(rowFor(page, `Kid-${id}`)).toHaveCount(0)
    await chooseAction(page, rowFor(page, `Home-${id}`), 'Move to trash')
    await expect(notice(page, `“Home-${id}” was moved to the trash.`)).toBeVisible()

    // Only the top of what was trashed is listed; the child is under it.
    await openTrash(page)
    await expect(page.getByTestId('trash-list')).not.toContainText(`Kid-${id}`)
    await page.getByRole('button', { name: `Restore Home-${id}` }).click()
    await expect(notice(page, `“Home-${id}” was restored.`)).toBeVisible()

    // The parent is back, and the child is now the top of its own piece of trash.
    await expect(page.getByTestId('trash-list')).toContainText(`Kid-${id}`)
    await expect(page.getByTestId('trash-list')).not.toContainText(`Home-${id}`)
  })

  test('a page in the trash that is opened says so and can be restored from there', async ({
    page,
  }, testInfo) => {
    const id = suffix(testInfo)
    await page.goto('/')
    const row = await createPageNamed(page, `Opened-${id}`)
    const pageId = await pageIdOf(row)
    await chooseAction(page, row, 'Move to trash')
    await expect(notice(page, `“Opened-${id}” was moved to the trash.`)).toBeVisible()

    await page.goto(`/p/${pageId}`)

    // The sync pill is a status too, so the banner is found by its words.
    const banner = page.getByRole('status').filter({ hasText: 'This page is in the trash.' })
    await expect(banner).toBeVisible()
    await banner.getByRole('button', { name: 'Restore' }).click()
    await expect(banner).toHaveCount(0)
  })

  test('a page that does not exist says so', async ({ page }) => {
    await page.goto('/p/00000000-0000-0000-0000-000000000000')

    await expect(
      page.getByRole('heading', { level: 1, name: 'This page could not be found' }),
    ).toBeVisible()
  })
})

test.describe('on a phone', () => {
  test.skip(({ isMobile }) => !isMobile, 'The drawer exists only on a narrow screen')

  test('the pages are in a drawer behind a button, which closes when a page is chosen', async ({
    page,
  }, testInfo) => {
    const title = `Drawer-${suffix(testInfo)}`
    await page.goto('/')
    await expect(page.getByRole('tree')).toHaveCount(0)
    const row = await createPageNamed(page, title)

    await expect(page.getByRole('dialog', { name: 'Navigation' })).toBeVisible()
    await row.click()

    await expect(page.getByRole('dialog')).toBeHidden()
    await expect(page.getByRole('heading', { level: 1, name: title, exact: true })).toBeVisible()
  })

  test('the drawer closes with Escape and opens again', async ({ page }) => {
    await page.goto('/')

    await openSidebar(page)
    await page.keyboard.press('Escape')
    await expect(page.getByRole('dialog')).toBeHidden()
    await openSidebar(page)
    await expect(page.getByRole('dialog')).toBeVisible()
  })

  test('every control in the sidebar is large enough to touch', async ({ page }, testInfo) => {
    await page.goto('/')
    await createPageNamed(page, `Touch-${suffix(testInfo)}`)

    const small: string[] = []
    for (const control of await page
      .getByRole('dialog')
      .locator('button:visible, a:visible, [role="treeitem"]:visible')
      .all()) {
      const box = await control.boundingBox()
      // The arrow and the menu button sit inside a 44 px line, so the line is the target; they are 28 px.
      if (box && box.height < 28) small.push((await control.textContent())?.trim() ?? '?')
    }
    expect(small).toEqual([])
    const lines = await page.locator('[role="treeitem"]').all()
    for (const line of lines) expect((await line.boundingBox())!.height).toBeGreaterThanOrEqual(44)
  })

  test('the new page can be named in the open drawer', async ({ page }, testInfo) => {
    const title = `Named-${suffix(testInfo)}`
    await page.goto('/')

    await createPageNamed(page, title)

    await expect(page.getByRole('dialog')).toBeVisible()
    expect(await levelOf(rowFor(page, title))).toBe(1)
  })
})

test('the sidebar and the page area do not scroll sideways', async ({ page }) => {
  await page.goto('/')
  await openSidebar(page)

  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  )
  expect(overflow).toBeLessThanOrEqual(0)
})
