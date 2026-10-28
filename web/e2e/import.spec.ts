import { Buffer } from 'node:buffer'
import type { Locator, Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { chooseLanguage, closeDrawer, openSidebar, openUserMenu, rowFor } from './helpers'
import { generateJex, type GeneratedJex } from './jex'

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
async function importJex(page: Page, jex: GeneratedJex) {
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
