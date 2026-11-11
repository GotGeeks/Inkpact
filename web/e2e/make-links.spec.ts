import type { Page, TestInfo } from '@playwright/test'
import { expect, test } from './fixtures'
import { closeDrawer, createPageNamed, openSidebar, pageIdOf, rowFor } from './helpers'

/**
 * Making links from the editor (run B.1b): `[[`, the slash menu, Ctrl+K on selected text, and the phone's toolbar button.
 * The links the editor makes are `/p/<id>`, which the server indexes: a link made here shows under "Linked from" on the other
 * page after its compaction (the e2e server compacts a few seconds after the last edit, see playwright.config.ts).
 */

const unique = (testInfo: TestInfo, name: string) =>
  `${name} ${testInfo.project.name} ${Date.now().toString(36)}`

const editorBody = (page: Page) => page.getByTestId('editor').locator('.ProseMirror')

/** Makes a page, opens it and waits until it can be typed into. Returns its id. */
async function newPage(page: Page, title: string) {
  await page.goto('/')
  const id = await pageIdOf(await createPageNamed(page, title))
  await closeDrawer(page)
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
  await expect(editorBody(page)).toHaveAttribute('contenteditable', 'true')
  return id
}

/** Opens another page from the sidebar (on a phone the drawer closes behind it). */
async function openPage(page: Page, title: string) {
  await openSidebar(page)
  await rowFor(page, title).click()
  await closeDrawer(page)
  await expect(page.getByTestId('page-title')).toHaveValue(title)
}

/** Selects the last `count` characters before the caret, as a person does with the keyboard. */
async function selectBack(page: Page, count: number) {
  for (let i = 0; i < count; i++) await page.keyboard.press('Shift+ArrowLeft')
}

/** The links of the page: text and address, in order. */
const linksOf = (page: Page) =>
  editorBody(page)
    .locator('a')
    .evaluateAll((anchors) =>
      anchors.map((a) => [a.textContent, a.getAttribute('href')] as [string | null, string | null]),
    )

test('[[ makes a link to a page, which then lists the page it is on under "Linked from"', async ({
  page,
}, testInfo) => {
  test.setTimeout(180_000)
  const targetTitle = unique(testInfo, 'Target')
  const sourceTitle = unique(testInfo, 'Source')
  const targetId = await newPage(page, targetTitle)
  await newPage(page, sourceTitle)

  await editorBody(page).click()
  await page.keyboard.type('see [[' + targetTitle)
  const menu = page.getByTestId('page-link-menu')
  await expect(menu).toBeVisible()
  await expect(menu.getByRole('option', { name: targetTitle, exact: true })).toBeVisible()
  await page.keyboard.press('Enter')

  await expect(menu).toBeHidden()
  expect(await linksOf(page)).toEqual([[targetTitle, `/p/${targetId}`]])
  // What is typed next is not part of the link.
  await page.keyboard.type('s now')
  expect(await linksOf(page)).toEqual([[targetTitle, `/p/${targetId}`]])

  // The server indexes the link when the page is compacted: the other page lists this one.
  await openPage(page, targetTitle)
  await expect(async () => {
    // Finish startup before reloading; aborting its fetches produces access-control errors in WebKit.
    await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
    await page.reload()
    await expect(page.getByTestId('backlinks')).toContainText(sourceTitle, { timeout: 4_000 })
  }).toPass({ timeout: 90_000, intervals: [2_000] })
})

test('"Link to page" in the slash menu opens the menu of pages', async ({ page }, testInfo) => {
  const targetTitle = unique(testInfo, 'Slash target')
  const targetId = await newPage(page, targetTitle)
  await newPage(page, unique(testInfo, 'Slash source'))

  await editorBody(page).click()
  await page.keyboard.type('/link')
  await expect(
    page.getByTestId('slash-menu').getByRole('option', { name: /Link to page/ }),
  ).toBeVisible()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('page-link-menu')).toBeVisible()
  await page.keyboard.type(targetTitle)
  await expect(
    page.getByTestId('page-link-menu').getByRole('option', { name: targetTitle, exact: true }),
  ).toBeVisible()
  await page.keyboard.press('Enter')

  await expect.poll(() => linksOf(page)).toEqual([[targetTitle, `/p/${targetId}`]])
})

test('Escape closes the menu of pages and keeps what was typed', async ({ page }, testInfo) => {
  await newPage(page, unique(testInfo, 'Escape'))

  await editorBody(page).click()
  await page.keyboard.type('[[pla')
  await expect(page.getByTestId('page-link-menu')).toBeVisible()
  await page.keyboard.press('Escape')

  await expect(page.getByTestId('page-link-menu')).toBeHidden()
  await expect(editorBody(page)).toContainText('[[pla')
  expect(await linksOf(page)).toEqual([])
})

test('a page address typed into the text is stored as /p/<id>', async ({
  page,
  baseURL,
}, testInfo) => {
  const targetId = await newPage(page, unique(testInfo, 'Address target'))
  await newPage(page, unique(testInfo, 'Address source'))

  await editorBody(page).click()
  // A space after an address makes a link of it; the editor stores a page of this app as /p/<id>.
  await page.keyboard.type(`${baseURL}/p/${targetId} `)

  await expect.poll(() => linksOf(page)).toEqual([[`${baseURL}/p/${targetId}`, `/p/${targetId}`]])
})

test.describe('Ctrl+K on selected text', () => {
  test.skip(({ isMobile }) => isMobile, 'a keyboard shortcut: the phone has the toolbar button')

  test('links it to a page or to a web address, and without a selection it opens search', async ({
    page,
  }, testInfo) => {
    const targetTitle = unique(testInfo, 'Ctrl target')
    const targetId = await newPage(page, targetTitle)
    await newPage(page, unique(testInfo, 'Ctrl source'))
    await editorBody(page).click()
    await page.keyboard.type('hello world')

    // Nothing selected: search, as everywhere.
    await page.keyboard.press('ControlOrMeta+k')
    await expect(page.getByTestId('search-dialog')).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(page.getByTestId('search-dialog')).toBeHidden()
    // Back in the text (the dialog gave the focus back to the page, not to the caret).
    await editorBody(page).click()
    await page.keyboard.press('End')

    // "world" selected: the picker, to a page.
    await selectBack(page, 5)
    await page.keyboard.press('ControlOrMeta+k')
    const picker = page.getByTestId('link-picker')
    await expect(picker).toBeVisible()
    await expect(page.getByTestId('search-dialog')).toBeHidden()
    await page.getByTestId('link-picker-input').fill(targetTitle)
    await expect(picker.getByRole('option', { name: targetTitle, exact: true })).toBeVisible()
    await page.keyboard.press('Enter')
    await expect(picker).toBeHidden()
    expect(await linksOf(page)).toEqual([['world', `/p/${targetId}`]])
    await expect(editorBody(page)).toContainText('hello world')
    // The focus is back in the text, after the link.
    await page.keyboard.type('!')
    await expect(editorBody(page)).toContainText('hello world!')
    expect(await linksOf(page)).toEqual([['world', `/p/${targetId}`]])

    // "hello" selected: a web address.
    await page.keyboard.press('Control+Home')
    await page.keyboard.press('Shift+ArrowRight')
    for (let i = 0; i < 4; i++) await page.keyboard.press('Shift+ArrowRight')
    await page.keyboard.press('ControlOrMeta+k')
    await page.getByTestId('link-picker-input').fill('https://example.com/docs')
    await expect(picker.getByRole('option').first()).toContainText(
      'Link to https://example.com/docs',
    )
    await page.keyboard.press('Enter')
    await expect(picker).toBeHidden()
    await expect
      .poll(() => linksOf(page))
      .toEqual([
        ['hello', 'https://example.com/docs'],
        ['world', `/p/${targetId}`],
      ])
  })

  test('Escape gives the text back as it was, and a link can be changed and removed from its menu', async ({
    page,
  }, testInfo) => {
    const firstTitle = unique(testInfo, 'First target')
    const secondTitle = unique(testInfo, 'Second target')
    await newPage(page, firstTitle)
    const secondId = await newPage(page, secondTitle)
    await newPage(page, unique(testInfo, 'Menu source'))
    await editorBody(page).click()
    await page.keyboard.type('some words')
    await selectBack(page, 5)

    await page.keyboard.press('ControlOrMeta+k')
    await expect(page.getByTestId('link-picker')).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(page.getByTestId('link-picker')).toBeHidden()
    expect(await linksOf(page)).toEqual([])
    // Still selected: typing replaces it.
    await page.keyboard.press('ControlOrMeta+k')
    await page.getByTestId('link-picker-input').fill(firstTitle)
    await page.keyboard.press('Enter')
    await expect(page.getByTestId('link-picker')).toBeHidden()

    // The link menu: change where it points.
    await editorBody(page).getByRole('link', { name: 'words' }).click()
    await expect(page.getByTestId('link-menu')).toBeVisible()
    await page.getByTestId('link-change').click()
    await page.getByTestId('link-picker-input').fill(secondTitle)
    await page.keyboard.press('Enter')
    expect(await linksOf(page)).toEqual([['words', `/p/${secondId}`]])

    // And take it away: the text stays.
    await editorBody(page).getByRole('link', { name: 'words' }).click()
    await page.getByTestId('link-remove').click()
    expect(await linksOf(page)).toEqual([])
    await expect(editorBody(page)).toContainText('some words')
  })
})

test.describe('the phone', () => {
  test.skip(({ isMobile }) => !isMobile, 'the toolbar is for a touch screen')

  test('has a link button: the picker for selected text, otherwise the menu of pages', async ({
    page,
  }, testInfo) => {
    const targetTitle = unique(testInfo, 'Phone target')
    const targetId = await newPage(page, targetTitle)
    await newPage(page, unique(testInfo, 'Phone source'))
    await editorBody(page).click()
    await page.keyboard.type('hello world')
    await expect(page.getByTestId('editor-toolbar')).toBeVisible()

    // Selected text: the picker.
    await selectBack(page, 5)
    await page.getByTestId('toolbar-link').click()
    await expect(page.getByTestId('link-picker')).toBeVisible()
    await page.getByTestId('link-picker-input').fill(targetTitle)
    await page.keyboard.press('Enter')
    await expect(page.getByTestId('link-picker')).toBeHidden()
    expect(await linksOf(page)).toEqual([['world', `/p/${targetId}`]])

    // No selection: [[ and the menu of pages.
    await page.keyboard.press('End')
    await page.getByTestId('toolbar-link').click()
    await expect(page.getByTestId('page-link-menu')).toBeVisible()
    await expect(editorBody(page)).toContainText('[[')
    await page.keyboard.press('Escape')
    await expect(page.getByTestId('page-link-menu')).toBeHidden()
  })
})
