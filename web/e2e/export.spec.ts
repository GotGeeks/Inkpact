import { Buffer } from 'node:buffer'
import { readFile } from 'node:fs/promises'
import type { Download, Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { chooseLanguage, closeDrawer, createPageNamed, openUserMenu } from './helpers'

const unique = () => `Export ${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`

/** From the account menu, the way a person gets there. On a phone the menu is in the drawer. */
async function takeExport(page: Page, label: string): Promise<Download> {
  await openUserMenu(page)
  const link = page.getByRole('menuitem', { name: label, exact: true })
  await expect(link).toBeVisible()
  const [download] = await Promise.all([page.waitForEvent('download'), link.click()])
  return download
}

async function bytesOf(download: Download) {
  expect(await download.failure()).toBeNull()
  return readFile(await download.path())
}

test('the account menu downloads the library as a zip that is whole', async ({ page }) => {
  const title = unique()
  await page.goto('/')
  await createPageNamed(page, title)
  await closeDrawer(page)

  const download = await takeExport(page, 'Export everything')

  expect(download.suggestedFilename()).toMatch(/^inkpact-.+-\d{4}-\d{2}-\d{2}\.zip$/)
  const zip = await bytesOf(download)
  // A local file header at the start, and the end of the central directory at the end: the zip was closed properly.
  expect(zip.subarray(0, 4).toString('latin1')).toBe('PK\u0003\u0004')
  expect(zip.subarray(-22).toString('latin1').startsWith('PK\u0005\u0006')).toBe(true)
  // Names are stored uncompressed.
  expect(zip.includes(Buffer.from(`${title}.md`))).toBe(true)
  expect(zip.includes(Buffer.from('README.md'))).toBe(true)
})

test('the export is offered in Dutch', async ({ page }) => {
  await page.goto('/')
  await chooseLanguage(page, 'Nederlands')

  const download = await takeExport(page, 'Alles exporteren')

  expect(download.suggestedFilename()).toMatch(/\.zip$/)
  const zip = await bytesOf(download)
  expect(zip.includes(Buffer.from('README.md'))).toBe(true)
})
