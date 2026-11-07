import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { openSidebar, openUserMenu, pageIdOf, rowFor } from './helpers'
import { generateLargeJex } from './jex'
import { newPage, openClient, typeText, type Client } from './syncClients'

/**
 * Edits a Sync would delete are kept as a copy (docs/architecture.md, sync flow, step 7): a browser that typed offline
 * into a block that another browser deleted meanwhile must not lose it. Run 1.21 (#48): this also holds for a page that is
 * larger than a sync message, which the Joplin import makes.
 */

const unique = (project: string, name: string) => `${name}-${project}-${Date.now().toString(36)}`

const OFFLINE = ' typed-offline-1234'

/** The sidebar's lines, in order. */
async function treeTitles(page: Page) {
  await openSidebar(page)
  return page.locator('[role="treeitem"]').allInnerTexts()
}

/**
 * `b` removes the last block of the page by joining it to the one before (Home, Backspace), and the server has it when this
 * returns. That matters: a deletion that reaches the other browser after it is back online is a live update, which is not
 * checked. `lastText` is the text of the last block.
 */
async function removeLastBlock(b: Client, lastText: string, mergedText: RegExp) {
  // ProseMirror reads a cursor that the browser moved (Home) on the next selectionchange event, and a key pressed before
  // that is applied to the old selection or dropped. This listener comes after ProseMirror's, so what it saw, ProseMirror
  // has seen too.
  await b.page.evaluate(() => {
    document.addEventListener('selectionchange', () => {
      const selection = getSelection()
      document.body.dataset.selectionSeen = `${selection?.anchorNode?.textContent ?? ''}@${selection?.anchorOffset ?? -1}`
    })
  })
  await b.editor.click()
  // Tried again until the cursor is at the start of the last block: under load (WebKit above all) Home can come before the
  // cursor has reached the end, and it changes nothing once the cursor is where it should be.
  await expect(async () => {
    await b.page.keyboard.press('Control+End')
    await b.page.keyboard.press('Home')
    await expect(b.page.locator('body')).toHaveAttribute('data-selection-seen', `${lastText}@0`, {
      timeout: 1_500,
    })
  }).toPass({ timeout: 15_000 })
  await b.page.keyboard.press('Backspace')
  await expect(b.editor.locator('p').last()).toHaveText(mergedText)
  // The server has confirmed every change of this tab.
  await expect(b.status).toHaveAttribute('data-unsent', 'false', { timeout: 30_000 })
}

/** `a` is back online: it says its edits are in a copy, and the copy is the next page, with the edits in it. */
async function expectCopyBesideTheOriginal(a: Client, title: string, lastBlock: string) {
  const notices = a.page.getByRole('region', { name: /Notifications/ })
  await expect(notices.getByText(/It is kept in a copy beside it\./)).toBeVisible({
    timeout: 60_000,
  })

  const copyTitle = `${title} (your offline changes)`

  // Opened from the notice, with what was typed offline where it was typed. (Before the sidebar is opened: on a phone it is
  // a dialog, which Escape closes, and the notice with it.)
  const original = a.page.url()
  await notices.getByRole('button', { name: 'Open the copy' }).click()
  await expect(a.page).not.toHaveURL(original)
  await expect(a.page.getByTestId('page-title')).toHaveValue(copyTitle)
  await expect(a.status).toHaveAttribute('data-state', 'live', { timeout: 60_000 })
  await expect(a.editor.locator('p').last()).toHaveText(lastBlock, { timeout: 60_000 })

  // And right after the page it is a copy of.
  await expect
    .poll(
      async () => {
        const titles = (await treeTitles(a.page)).map((text) => text.trim())
        const at = titles.findIndex((text) => text.includes(title))
        return titles[at + 1]?.includes(copyTitle) ?? false
      },
      { timeout: 30_000, message: 'the copy should be right after the page' },
    )
    .toBe(true)
}

test('offline edits that a change from elsewhere deleted are kept in a copy beside the page', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const title = unique(testInfo.project.name, 'Kept')
  const id = await newPage(page, title)
  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]
  await typeText(a, 'first')
  await a.page.keyboard.press('Enter')
  await a.page.keyboard.type('second')
  await expect(b.editor).toContainText('second')

  a.setOffline(true)
  await expect(a.status).toHaveAttribute('data-state', 'offline')
  await typeText(a, OFFLINE)
  await removeLastBlock(b, 'second', /^firstsecond$/)

  a.setOffline(false)

  // The page now has what the other browser made of it; the copy has the page as the first browser had it.
  await expect(a.editor.locator('p').last()).toHaveText('firstsecond', { timeout: 60_000 })
  await expectCopyBesideTheOriginal(a, title, `second${OFFLINE}`)
  for (const client of [a, b]) await client.context.close()
})

test('offline edits to a page larger than a sync message, as an imported note is, are kept in a copy as well', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  // One browser of the suite is enough: the size is what is tested, and a phone adds minutes of upload for nothing more.
  test.skip(testInfo.project.name !== 'desktop', 'checked on the desktop')
  test.setTimeout(420_000)
  const jex = generateLargeJex(unique(testInfo.project.name, 'Copy'), 6_000_000)

  // The page is made the way such a page comes about: by importing a note of several MiB.
  await page.goto('/')
  await openUserMenu(page)
  await page.getByRole('menuitem', { name: 'Import from Joplin', exact: true }).click()
  await expect(page.getByTestId('import-choose')).toBeVisible()
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
  await openSidebar(page)
  await rowFor(page, jex.titles.notebook)
    .getByRole('button', { name: `Expand ${jex.titles.notebook}` })
    .click()
  const id = await pageIdOf(rowFor(page, jex.titles.note))

  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]
  await expect(a.editor).toContainText(jex.lastLine, { timeout: 60_000 })
  await expect(b.editor).toContainText(jex.lastLine, { timeout: 60_000 })

  a.setOffline(true)
  await expect(a.status).toHaveAttribute('data-state', 'offline')
  await typeText(a, OFFLINE)
  await removeLastBlock(
    b,
    jex.lastLine,
    new RegExp(`^Line \\d+: .*${jex.lastLine.replace('.', '\\.')}$`),
  )

  a.setOffline(false)

  await expectCopyBesideTheOriginal(a, jex.titles.note, `${jex.lastLine}${OFFLINE}`)
  // The whole page is in the copy, not just what was typed: its first line is there too.
  await expect(a.editor).toContainText(jex.firstLine, { timeout: 60_000 })
  for (const client of [a, b]) await client.context.close()
})
