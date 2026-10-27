import { Buffer } from 'node:buffer'
import type { BrowserContextOptions, Locator, Page, TestInfo } from '@playwright/test'
import { expect, test } from './fixtures'
import { closeDrawer, createPageNamed, pageIdOf } from './helpers'

/** A PNG of one pixel. */
const PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
  'base64',
)
const PDF = Buffer.from('%PDF-1.4\n% a test file\n')

const editorBody = (page: Page) => page.getByTestId('editor').locator('.ProseMirror')
const imageIn = (page: Page) => editorBody(page).locator('img[data-attachment-id]')
const unique = (project: string, name: string) => `${name}-${project}-${Date.now().toString(36)}`

async function openNewPage(page: Page, title: string) {
  await page.goto('/')
  const id = await pageIdOf(await createPageNamed(page, title))
  await closeDrawer(page)
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
  await expect(editorBody(page)).toHaveAttribute('contenteditable', 'true')
  return id
}

/** Types a slash command and picks it, and gives the file picker that opens. */
async function chooseFromSlashMenu(page: Page, query: string) {
  const chooser = page.waitForEvent('filechooser')
  await page.keyboard.type(`/${query}`)
  await expect(page.getByRole('option')).toHaveCount(1)
  await page.keyboard.press('Enter')
  return chooser
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

/** A tap; on the iPhone a click, as in editor-blocks.spec.ts (WebKit's tap adds a style element of its own). */
async function press(locator: Locator, testInfo: TestInfo) {
  if (testInfo.project.name === 'iphone') await locator.click()
  else await locator.tap()
}

test('an image from the slash menu is uploaded, shown, and there in a second browser', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  test.skip(
    testInfo.project.name !== 'desktop',
    'the slash menu with a keyboard belongs on the desktop',
  )
  const id = await openNewPage(page, unique(testInfo.project.name, 'Image'))
  await editorBody(page).click()

  const chooser = await chooseFromSlashMenu(page, 'image')
  expect(chooser.isMultiple()).toBe(true)
  await chooser.setFiles({ name: 'pixel.png', mimeType: 'image/png', buffer: PNG })

  await expectShown(imageIn(page))
  await expect(editorBody(page)).not.toContainText('/image')
  const src = await imageIn(page).getAttribute('src')

  const other = await browser.newContext({
    ...(testInfo.project.use as BrowserContextOptions),
    baseURL,
  })
  const second = await other.newPage()
  await second.goto(`/p/${id}`)
  await expect(imageIn(second)).toHaveAttribute('src', src!)
  await expectShown(imageIn(second))
  await other.close()
})

test('on a phone, the toolbar adds an image', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name === 'desktop', 'the toolbar is for touch screens')
  await openNewPage(page, unique(testInfo.project.name, 'Toolbar image'))
  await press(editorBody(page), testInfo)
  await expect(page.getByTestId('editor-toolbar')).toBeVisible()

  const chooser = page.waitForEvent('filechooser')
  await press(page.getByTestId('toolbar-image'), testInfo)
  await (await chooser).setFiles({ name: 'pixel.png', mimeType: 'image/png', buffer: PNG })

  await expectShown(imageIn(page))
})

test('a file shows its name and size and downloads as it was', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'downloads are the same on every device')
  await openNewPage(page, unique(testInfo.project.name, 'File'))
  await editorBody(page).click()
  await page.keyboard.type('Manual: ')

  const chooser = await chooseFromSlashMenu(page, 'file')
  await chooser.setFiles({ name: 'manual.pdf', mimeType: 'application/pdf', buffer: PDF })

  const file = editorBody(page).locator('a[data-attachment-id]')
  await expect(file).toContainText('manual.pdf')
  await expect(file).toContainText('bytes')
  await expect(file).toHaveAccessibleName(/^Download manual\.pdf, \d+ bytes$/)
  await expect(editorBody(page)).toContainText('Manual: ')

  const download = page.waitForEvent('download')
  await file.click()
  const saved = await download
  expect(saved.suggestedFilename()).toBe('manual.pdf')
  const chunks: Buffer[] = []
  for await (const chunk of await saved.createReadStream()) chunks.push(chunk as Buffer)
  expect(Buffer.concat(chunks)).toEqual(PDF)
})

test('a pasted screenshot is uploaded and shown', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'pasting a file is a desktop habit')
  await openNewPage(page, unique(testInfo.project.name, 'Paste'))
  await editorBody(page).click()

  await editorBody(page).evaluate((element, base64) => {
    const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))
    const data = new DataTransfer()
    data.items.add(new File([bytes], 'screenshot.png', { type: 'image/png' }))
    element.dispatchEvent(
      new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }),
    )
  }, PNG.toString('base64'))

  await expectShown(imageIn(page))
})

test('an empty file is refused with a message, and nothing is added', async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'the message is the same on every device')
  await openNewPage(page, unique(testInfo.project.name, 'Empty'))
  await editorBody(page).click()

  const chooser = await chooseFromSlashMenu(page, 'file')
  await chooser.setFiles({ name: 'empty.txt', mimeType: 'text/plain', buffer: Buffer.alloc(0) })

  // In the toast area: the toast's copy for screen readers has the same text.
  const notices = page.getByRole('region', { name: /Notifications/ })
  await expect(notices.getByText('That file is empty.', { exact: true })).toBeVisible()
  await expect(page.getByTestId('attachment-uploading')).toBeHidden()
  await expect(editorBody(page).locator('[data-attachment-id]')).toHaveCount(0)
})

test('an image opened on its own is a sandboxed document that loads without a violation', async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'the headers are the same on every device')
  await openNewPage(page, unique(testInfo.project.name, 'Direct'))
  await editorBody(page).click()
  const chooser = await chooseFromSlashMenu(page, 'image')
  await chooser.setFiles({ name: 'pixel.png', mimeType: 'image/png', buffer: PNG })
  await expectShown(imageIn(page))
  const src = (await imageIn(page).getAttribute('src'))!

  const response = (await page.goto(src))!

  expect(response.status()).toBe(200)
  const headers = response.headers()
  expect(headers['content-type']).toBe('image/png')
  expect(headers['content-disposition']).toMatch(/^inline;/)
  expect(headers['content-security-policy']).toContain('sandbox')
  expect(headers['x-content-type-options']).toBe('nosniff')
  // The fixture fails the test on any violation the image viewer would cause.
})
