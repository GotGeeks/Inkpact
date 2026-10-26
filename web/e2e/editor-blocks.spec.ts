import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { chooseLanguage, closeDrawer, createPageNamed, pageIdOf } from './helpers'

const pill = (page: Page) => page.getByTestId('sync-status')
const editorBody = (page: Page) => page.getByTestId('editor').locator('.ProseMirror')
const slashMenu = (page: Page) => page.getByTestId('slash-menu')
const toolbar = (page: Page) => page.getByTestId('editor-toolbar')

async function openNewPage(page: Page, title: string) {
  await page.goto('/')
  const id = await pageIdOf(await createPageNamed(page, title))
  await closeDrawer(page)
  await expect(pill(page)).toHaveAttribute('data-state', 'live')
  await expect(editorBody(page)).toHaveAttribute('contenteditable', 'true')
  return id
}

const unique = (project: string, name: string) => `${name}-${project}-${Date.now().toString(36)}`

/** Each block the menu makes, found by a word that only it has, and the element it leaves in the page. */
const blocks = [
  { query: 'h1', selector: 'h1' },
  { query: 'h2', selector: 'h2' },
  { query: 'h3', selector: 'h3' },
  { query: 'ul', selector: 'ul:not([data-type="taskList"]) li' },
  { query: 'ol', selector: 'ol li' },
  { query: 'todo', selector: 'ul[data-type="taskList"] li' },
  { query: 'quo', selector: 'blockquote' },
  { query: 'snippet', selector: 'pre code' },
  { query: 'grid', selector: 'table' },
  { query: 'hr', selector: 'hr' },
]

test('every block of the slash menu can be made with the keyboard alone, and is there for everybody', async ({
  page,
  context,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'a keyboard-only run belongs on the desktop')
  const id = await openNewPage(page, unique(testInfo.project.name, 'Slash'))
  const body = editorBody(page)
  await body.click()

  for (const block of blocks) {
    await page.keyboard.type(`/${block.query}`)
    await expect(slashMenu(page)).toBeVisible()
    await expect(page.getByRole('option')).toHaveCount(1)
    await expect(body).toHaveAttribute('aria-activedescendant', /^slash-option-/)
    await page.keyboard.press('Enter')
    await expect(slashMenu(page)).toBeHidden()
    await expect(body.locator(block.selector).first()).toBeAttached()
    // The empty paragraph TipTap keeps after the block is the next line.
    await page.keyboard.press('Control+End')
  }

  const expectBlocks = async (target: Page) => {
    const editor = editorBody(target)
    for (const block of blocks) await expect(editor.locator(block.selector).first()).toBeAttached()
    await expect(editor.locator('table th')).toHaveCount(3)
    await expect(editor.locator('table td')).toHaveCount(6)
    await expect(editor).not.toContainText('/')
  }
  await expectBlocks(page)

  // A second tab proves the server has it, and a reload that it is not only in memory.
  const other = await context.newPage()
  await other.goto(`/p/${id}`)
  await expectBlocks(other)
  await other.close()
  await page.reload()
  await expectBlocks(page)
})

test('the slash menu works with the mouse, filters, and closes with Escape and a space', async ({
  page,
}, testInfo) => {
  await openNewPage(page, unique(testInfo.project.name, 'SlashMouse'))
  const body = editorBody(page)
  await body.click()

  await page.keyboard.type('/')
  await expect(slashMenu(page)).toBeVisible()
  await expect(page.getByRole('option')).toHaveCount(11)
  await page.getByRole('option', { name: /^Heading 2/ }).click()
  await expect(body.locator('h2')).toHaveCount(1)
  // The focus stayed in the editor: typing goes into the heading.
  await page.keyboard.type('Title')
  await expect(body.locator('h2')).toHaveText('Title')

  await page.keyboard.press('Control+End')
  await page.keyboard.type('/zzz')
  await expect(slashMenu(page)).toContainText('No results')
  await page.keyboard.type(' more')
  await expect(slashMenu(page)).toBeHidden()
  await expect(body).toContainText('/zzz more')

  await page.keyboard.press('Enter')
  await page.keyboard.type('/qu')
  await expect(slashMenu(page)).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(slashMenu(page)).toBeHidden()
  await expect(body).toContainText('/qu')

  // Neither inside a word or an address, nor in a code block.
  await page.keyboard.press('Enter')
  await page.keyboard.type('https://example.com/ and/or')
  await expect(slashMenu(page)).toBeHidden()
})

test('the slash menu speaks Dutch and finds blocks by their Dutch words', async ({
  page,
}, testInfo) => {
  await openNewPage(page, unique(testInfo.project.name, 'SlashDutch'))
  await chooseLanguage(page, 'Nederlands')
  const body = editorBody(page)
  await body.click()

  await page.keyboard.type('/lijst')
  await expect(page.getByRole('option')).toHaveCount(2)
  await expect(page.getByRole('listbox')).toHaveAccessibleName('Een blok invoegen')
  await page.keyboard.press('ArrowDown')
  await page.keyboard.press('Enter')
  await expect(body.locator('ol li')).toHaveCount(1)
})

test('the toolbar never appears on a desktop with a mouse', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'only the desktop has a wide screen and a mouse')
  await openNewPage(page, unique(testInfo.project.name, 'NoToolbar'))
  await editorBody(page).click()
  await page.keyboard.type('text')
  await expect(toolbar(page)).toHaveCount(0)
})

test.describe('on a phone', () => {
  test.beforeEach(({}, testInfo) => {
    test.skip(testInfo.project.name === 'desktop', 'needs a touch screen')
  })

  /** The browser's visual viewport shrinks when the keyboard opens; Playwright has no keyboard, so a test makes one. */
  async function fakeKeyboard(page: Page) {
    await page.addInitScript(() => {
      const viewport = Object.assign(new EventTarget(), {
        width: window.innerWidth,
        height: window.innerHeight,
        offsetLeft: 0,
        offsetTop: 0,
        pageLeft: 0,
        pageTop: 0,
        scale: 1,
      })
      Object.defineProperty(window, 'visualViewport', { value: viewport, configurable: true })
      ;(window as unknown as Record<string, unknown>).__setKeyboard = (height: number) => {
        viewport.height = window.innerHeight - height
        viewport.dispatchEvent(new Event('resize'))
      }
    })
  }

  test('the toolbar follows the editor focus, undoes and redoes, and sits above the keyboard', async ({
    page,
  }, testInfo) => {
    await fakeKeyboard(page)
    await openNewPage(page, unique(testInfo.project.name, 'Toolbar'))
    await expect(toolbar(page)).toHaveCount(0)

    const body = editorBody(page)
    await body.tap()
    await expect(toolbar(page)).toBeVisible()
    await expect(toolbar(page)).toHaveAttribute('aria-label', 'Formatting')
    const undo = page.getByRole('button', { name: 'Undo' })
    const redo = page.getByRole('button', { name: 'Redo' })
    await expect(undo).toHaveAttribute('aria-disabled', 'true')

    await page.keyboard.type('hello world')
    await expect(undo).not.toHaveAttribute('aria-disabled', 'true')
    await undo.tap()
    await expect(body).not.toContainText('hello world')
    await expect(redo).not.toHaveAttribute('aria-disabled', 'true')
    await redo.tap()
    await expect(body).toContainText('hello world')
    // Pressing the buttons never took the focus from the text.
    await expect(body).toBeFocused()

    // Bold, then more text: the button says it is on.
    const bold = page.getByRole('button', { name: 'Bold' })
    await bold.tap()
    await expect(bold).toHaveAttribute('aria-pressed', 'true')
    await page.keyboard.type(' strong')
    await expect(body.locator('strong')).toHaveText('strong')

    // At the bottom of the screen without a keyboard.
    const viewportHeight = page.viewportSize()!.height
    const resting = await toolbar(page).boundingBox()
    expect(Math.round(resting!.y + resting!.height)).toBe(viewportHeight)

    // With the keyboard up, it moves up by as much as the keyboard covers.
    await page.evaluate(() =>
      (window as unknown as { __setKeyboard: (h: number) => void }).__setKeyboard(300),
    )
    await expect
      .poll(async () =>
        Math.round(
          (await toolbar(page).boundingBox())!.y + (await toolbar(page).boundingBox())!.height,
        ),
      )
      .toBe(viewportHeight - 300)

    // The toolbar goes with the focus: tapping outside the text, in the title, hides it.
    await page.getByTestId('page-title').tap()
    await expect(toolbar(page)).toHaveCount(0)
    await expect
      .poll(() =>
        page.evaluate(() => document.documentElement.style.getPropertyValue('--keyboard-inset')),
      )
      .toBe('')
  })

  test('the caret stays above the toolbar at the bottom of a long page', async ({
    page,
  }, testInfo) => {
    await fakeKeyboard(page)
    await openNewPage(page, unique(testInfo.project.name, 'LongPage'))
    const body = editorBody(page)
    await body.tap()
    for (let line = 1; line <= 40; line++) await page.keyboard.type(`line ${line}\n`)
    await page.evaluate(() =>
      (window as unknown as { __setKeyboard: (h: number) => void }).__setKeyboard(300),
    )
    await expect(toolbar(page)).toBeVisible()

    await page.keyboard.type('the last one')
    const caretBottom = () =>
      page.evaluate(() => {
        const range = window.getSelection()!.getRangeAt(0).cloneRange()
        range.collapse(true)
        return range.getBoundingClientRect().bottom
      })
    await expect
      .poll(async () => {
        const bar = (await toolbar(page).boundingBox())!
        return (await caretBottom()) <= bar.y
      })
      .toBe(true)
  })

  test('the slash button opens the menu, which stays out from under the toolbar', async ({
    page,
  }, testInfo) => {
    await fakeKeyboard(page)
    await openNewPage(page, unique(testInfo.project.name, 'SlashButton'))
    const body = editorBody(page)
    await body.tap()
    await page.keyboard.type('word')

    await page.getByRole('button', { name: 'Insert a block' }).tap()
    await expect(body).toContainText('word /')
    await expect(slashMenu(page)).toBeVisible()
    await expect(body).toBeFocused()

    await page.getByRole('option', { name: /^Bullet list/ }).tap()
    await expect(body.locator('ul li')).toHaveCount(1)
    await expect(slashMenu(page)).toBeHidden()

    // The menu never reaches below the top of the toolbar.
    await page.keyboard.press('Control+End')
    await page.getByRole('button', { name: 'Insert a block' }).tap()
    await expect(slashMenu(page)).toBeVisible()
    const menuBox = (await slashMenu(page).boundingBox())!
    const barBox = (await toolbar(page).boundingBox())!
    expect(menuBox.y + menuBox.height).toBeLessThanOrEqual(barBox.y + 1)
  })
})
