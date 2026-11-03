import { Buffer } from 'node:buffer'
import type { Locator, Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { chooseLanguage, closeDrawer, openSidebar, openUserMenu, rowFor } from './helpers'
import {
  generateJex,
  generateLargeJex,
  generateLinkedJex,
  type GeneratedJex,
  type LargeJex,
  type LinkedJex,
} from './jex'

const editorBody = (page: Page) => page.getByTestId('editor').locator('.ProseMirror')

const unique = (project: string) => `${project}-${Date.now().toString(36)}`

/** From the account menu, the way a person gets there. On a phone the menu is in the drawer, which closes behind it. */
async function openImportPage(page: Page, menuItem = 'Import from Joplin') {
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
async function importJex(page: Page, jex: GeneratedJex | LinkedJex | LargeJex) {
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

const countOf = (page: Page, kind: string) => page.getByTestId(`import-count-${kind}`)

function reportRow(page: Page, title: string): Locator {
  return page.getByTestId('import-report').getByRole('listitem').filter({ hasText: title }).first()
}

/** The image has been loaded from the server and drawn: the address, the session and the policy all let it through. */
async function expectShown(image: Locator) {
  await expect(image).toHaveAttribute('src', /^\/api\/attachments\/[0-9a-f-]{36}$/)
  await expect
    .poll(() =>
      image.evaluate((element: HTMLImageElement) => element.complete && element.naturalWidth),
    )
    .toBe(1)
}

test('a Joplin export is imported and reported, and its notes open with their picture and file', async ({
  page,
}, testInfo) => {
  const jex = generateJex(unique(testInfo.project.name))
  await openImportPage(page)

  await importJex(page, jex)

  await expect(page.getByTestId('import-result')).toHaveText(
    'The import has finished, with some things to look at',
  )
  await expect(countOf(page, 'notebooks')).toContainText('2')
  await expect(countOf(page, 'notes')).toContainText('3')
  await expect(countOf(page, 'todos')).toContainText('1')
  await expect(countOf(page, 'tags')).toContainText('1')
  await expect(countOf(page, 'images')).toContainText('1')
  await expect(countOf(page, 'files')).toContainText('1')

  // What did not come across as it was, with the reason in words.
  const clipping = reportRow(page, jex.titles.clipping)
  await expect(clipping).toContainText('Imported, with changes')
  await expect(clipping).toContainText('The “svg” element was removed, with what was inside it.')
  const secret = reportRow(page, jex.titles.secret)
  await expect(secret).toContainText('Skipped')
  await expect(secret).toContainText('It is encrypted in the export, so it was skipped.')
  // An item that came across whole is not in the report; a skipped one has no page to link to.
  await expect(reportRow(page, jex.titles.pictures)).toHaveCount(0)
  await expect(secret.getByRole('link')).toHaveCount(0)

  // Nothing runs off the side of a phone.
  await expect
    .poll(() =>
      page.evaluate(() => {
        const main = document.querySelector('main')!
        return main.scrollWidth - main.clientWidth
      }),
    )
    .toBeLessThanOrEqual(0)

  // The sidebar has heard of the pages the server made.
  await openSidebar(page)
  await expect(rowFor(page, jex.titles.notebook)).toBeVisible()
  await closeDrawer(page)

  // The clipping links to its page, and what is in it is there, without the picture that did not come across.
  await expect(clipping.getByRole('link', { name: jex.titles.clipping })).toBeVisible()
  await page.getByTestId('import-report').getByRole('link').first().click()
  await expect(page).toHaveURL(/\/p\//)
  await expect(editorBody(page)).toContainText('Hello')
  await expect(editorBody(page)).not.toContainText('circle')
})

test('an imported notebook lists its sub-notebook and its notes, and each opens', async ({
  page,
}, testInfo) => {
  const jex = generateJex(unique(testInfo.project.name))
  await openImportPage(page)
  await importJex(page, jex)

  await openSidebar(page)
  await rowFor(page, jex.titles.notebook).click()
  await closeDrawer(page)

  // Sub-notebooks first, then notes, as in the sidebar; the sub-notebook says how many pages it holds (the encrypted
  // note was skipped, so two).
  const list = page.getByTestId('child-pages')
  await expect(list.getByRole('heading')).toHaveText('Pages in here')
  await expect(list.getByRole('listitem')).toHaveText([
    `${jex.titles.subNotebook}2 pages`,
    jex.titles.pictures,
  ])

  await list.getByRole('link', { name: jex.titles.pictures }).click()
  await expect(editorBody(page)).toContainText('Rent and bold words.')
  await expect(page.getByTestId('child-pages')).toHaveCount(0)
})

test('the pictures page has its image and its file', async ({ page }, testInfo) => {
  const jex = generateJex(unique(testInfo.project.name))
  await openImportPage(page)
  await importJex(page, jex)

  // The page is in the tree, one notebook down; the report does not list it because it came across whole.
  await openSidebar(page)
  const notebook = rowFor(page, jex.titles.notebook)
  await notebook.getByRole('button', { name: `Expand ${jex.titles.notebook}` }).click()
  await rowFor(page, jex.titles.pictures).click()
  await closeDrawer(page)

  await expect(editorBody(page)).toContainText('Rent and bold words.')
  await expectShown(editorBody(page).locator('img[data-attachment-id]'))
  const file = editorBody(page).locator('a[data-attachment-id]')
  await expect(file).toContainText('manual.pdf')
})

test('the same export again changes nothing, and the address still shows how an import went', async ({
  page,
}, testInfo) => {
  const jex = generateJex(unique(testInfo.project.name))
  await openImportPage(page)
  await importJex(page, jex)
  await expect(countOf(page, 'notes')).toContainText('3')

  await page.getByRole('button', { name: 'Import another file' }).click()
  await expect(page).toHaveURL(/\/import\/joplin$/)
  await importJex(page, jex)

  await expect(countOf(page, 'notebooks')).toContainText('0')
  await expect(countOf(page, 'notes')).toContainText('0')
  await expect(page.getByTestId('import-left')).toContainText('already imported earlier')

  // A reload, or a link to this address, shows the same import again.
  await page.reload()
  await expect(page.getByTestId('import-result')).toBeVisible()
  await expect(page.getByTestId('import-left')).toContainText('already imported earlier')
})

test('the import page is in Dutch for someone who chose Dutch', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'the language is the same on every device')
  const jex = generateJex(unique(testInfo.project.name))
  await page.goto('/')
  await chooseLanguage(page, 'Nederlands')

  await openUserMenu(page)
  await page.getByRole('menuitem', { name: 'Importeren uit Joplin', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Importeren uit Joplin' })).toBeVisible()
  await importJex(page, jex)

  await expect(page.getByTestId('import-result')).toHaveText(
    'De import is klaar, met een paar dingen om te bekijken',
  )
  await expect(countOf(page, 'notebooks')).toContainText('Notitieboeken')
  await expect(reportRow(page, jex.titles.clipping)).toContainText('Geïmporteerd, met wijzigingen')
  await expect(reportRow(page, jex.titles.secret)).toContainText('Overgeslagen')
})

test('a file that is not a Joplin export is refused with a message', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'the message is the same on every device')
  await openImportPage(page)

  // Another device's import may be running in the shared workspace: then the answer is that one, and this tries again.
  await expect(async () => {
    await page.getByTestId('import-file').setInputFiles({
      name: 'notes.jex',
      mimeType: 'application/octet-stream',
      buffer: Buffer.from('this is not a tar file, only some text. '.repeat(40)),
    })
    await expect(page.getByTestId('import-error')).toContainText('That is not a Joplin export.', {
      timeout: 3_000,
    })
  }).toPass({ timeout: 120_000, intervals: [1_000, 2_000, 4_000] })
  await expect(page.getByTestId('import-choose')).toBeVisible()
})

test('a link between two imported notes opens the other note, and back', async ({
  page,
}, testInfo) => {
  const jex = generateLinkedJex(unique(testInfo.project.name))
  await openImportPage(page)
  await importJex(page, jex)
  // Both notes are in the export, so nothing about their links needs a line in the report.
  await expect(page.getByTestId('import-report')).toHaveCount(0)

  await openSidebar(page)
  await rowFor(page, jex.titles.notebook)
    .getByRole('button', { name: `Expand ${jex.titles.notebook}` })
    .click()
  await rowFor(page, jex.titles.first).click()
  await closeDrawer(page)
  await expect(editorBody(page)).toContainText('Start of the trail.')

  // A click places the cursor in the link, as in any text; the menu under it opens the page.
  const start = page.url()
  await editorBody(page).getByRole('link', { name: 'Go to the destination' }).click()
  await expect(page.getByTestId('link-open')).toHaveText('Open page')
  expect(page.url()).toBe(start)
  await page.getByTestId('link-open').click()
  await expect(editorBody(page)).toContainText('Destination page text.')
  await expect(page).toHaveURL(/\/p\/[0-9a-f-]{36}$/)
  expect(page.url()).not.toBe(start)

  await editorBody(page).getByRole('link', { name: 'Back to the start' }).click()
  await page.getByTestId('link-open').click()
  await expect(editorBody(page)).toContainText('Start of the trail.')
  // One tab: opening a page did not open another.
  expect(page.context().pages()).toHaveLength(1)
})

test('a note of about 6 MB imports as content, opens with a notice, and stays in step with the server', async ({
  page,
}, testInfo) => {
  // A phone adds a minute of upload for nothing more: the page, the notice and the sync are the same.
  test.skip(testInfo.project.name !== 'desktop', 'checked on the desktop')
  test.setTimeout(240_000)
  const jex = generateLargeJex(unique(testInfo.project.name), 6_000_000)
  await openImportPage(page)
  await importJex(page, jex)
  // Content, not a file: nothing in the report says it was stored as one.
  await expect(page.getByTestId('import-report')).toHaveCount(0)

  await openSidebar(page)
  await rowFor(page, jex.titles.notebook)
    .getByRole('button', { name: `Expand ${jex.titles.notebook}` })
    .click()
  await rowFor(page, jex.titles.note).click()
  await closeDrawer(page)

  await expect(page.getByTestId('large-page')).toContainText('This page is large', {
    timeout: 60_000,
  })
  await expect(editorBody(page)).toContainText(jex.firstLine, { timeout: 60_000 })
  const status = page.getByTestId('sync-status')
  await expect(status).toHaveAttribute('data-state', 'live')

  // Not dropped and reconnecting: the connection stays live for a while, and a small edit reaches the server.
  await page.waitForTimeout(20_000)
  await expect(status).toHaveAttribute('data-state', 'live')
  await editorBody(page).click()
  await page.keyboard.press('Control+Home')
  await page.keyboard.type('Saved: ')
  await expect(status).toHaveAttribute('data-state', 'live')
  await page.waitForTimeout(2_000)
  await page.reload()
  await expect(editorBody(page)).toContainText('Saved: Line 0', { timeout: 60_000 })
})
