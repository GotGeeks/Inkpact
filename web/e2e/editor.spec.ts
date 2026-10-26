import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { closeDrawer, createPageNamed, openSidebar, pageIdOf, rowFor } from './helpers'

const pill = (page: Page) => page.getByTestId('sync-status')
const editorBody = (page: Page) => page.getByTestId('editor').locator('.ProseMirror')

async function openNewPage(page: Page, title: string) {
  await page.goto('/')
  const id = await pageIdOf(await createPageNamed(page, title))
  await closeDrawer(page)
  await expect(pill(page)).toHaveAttribute('data-state', 'live')
  await expect(editorBody(page)).toHaveAttribute('contenteditable', 'true')
  return id
}

/** Pastes HTML into the editor the way the browser does: a paste event with the clipboard's text/html. */
async function pasteHtml(page: Page, html: string) {
  await page.evaluate((payload) => {
    const target = document.querySelector('[data-testid="editor"] .ProseMirror')!
    const data = new DataTransfer()
    data.setData('text/html', payload)
    data.setData('text/plain', 'plain')
    target.dispatchEvent(
      new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }),
    )
  }, html)
}

const unique = (project: string, name: string) => `${name}-${project}-${Date.now().toString(36)}`

test('every kind of block can be made, causes no CSP violation, and is there after a reload', async ({
  page,
  context,
}, testInfo) => {
  const id = await openNewPage(page, unique(testInfo.project.name, 'Blocks'))
  const body = editorBody(page)
  await body.click()

  // Markdown shortcuts, the way a person types them.
  await page.keyboard.type('# Heading one\n')
  await page.keyboard.type('- bullet\n\n')
  await page.keyboard.type('1. numbered\n\n')
  await page.keyboard.type('[ ] task\n\n')
  await page.keyboard.type('> quoted\n\n')
  await page.keyboard.type('``` code line')
  await page.keyboard.press('ArrowDown')
  await page.keyboard.press('ArrowDown')
  await page.keyboard.type('see https://example.com/page ')
  await pasteHtml(
    page,
    '<table><tr><th>one</th><th>two</th></tr><tr><td>a</td><td>b</td></tr></table>',
  )

  const expectBlocks = async (target: Page) => {
    const editor = editorBody(target)
    await expect(editor.locator('h1')).toHaveText('Heading one')
    await expect(editor.locator('ul:not([data-type="taskList"]) li')).toHaveText('bullet')
    await expect(editor.locator('ol li')).toHaveText('numbered')
    await expect(editor.locator('ul[data-type="taskList"] li')).toContainText('task')
    await expect(editor.locator('blockquote')).toHaveText('quoted')
    await expect(editor.locator('pre code')).toContainText('code line')
    await expect(editor.locator('a[href="https://example.com/page"]')).toHaveCount(1)
    await expect(editor.locator('table th')).toHaveText(['one', 'two'])
    await expect(editor.locator('table td')).toHaveText(['a', 'b'])
  }
  await expectBlocks(page)

  // A second tab proves the server has it (it stores before it relays), and a reload proves it is not only in memory.
  const other = await context.newPage()
  await other.goto(`/p/${id}`)
  await expectBlocks(other)
  await other.close()
  await page.reload()
  await expectBlocks(page)
})

test('a checklist item can be ticked, and is crossed out when it is', async ({
  page,
}, testInfo) => {
  await openNewPage(page, unique(testInfo.project.name, 'Tasks'))
  await editorBody(page).click()
  await page.keyboard.type('[ ] write the test')

  const item = editorBody(page).locator('li[data-checked]')
  await expect(item).toHaveAttribute('data-checked', 'false')
  await item.getByRole('checkbox', { name: 'Not done' }).check()

  await expect(item).toHaveAttribute('data-checked', 'true')
  await expect(item.getByRole('checkbox', { name: 'Done' })).toBeChecked()
  await expect(item.locator('div').first()).toHaveCSS('text-decoration-line', 'line-through')
})

test('pasted HTML cannot run anything, and the text and the safe parts stay', async ({
  page,
}, testInfo) => {
  await openNewPage(page, unique(testInfo.project.name, 'Hostile'))
  await editorBody(page).click()

  await pasteHtml(
    page,
    [
      '<p>kept <strong>bold</strong></p>',
      '<img src="x" onerror="window.__pwned = 1">',
      '<script>window.__pwned = 1</script>',
      '<p onclick="window.__pwned = 1"><a href="javascript:window.__pwned = 1">bad link</a> <a href="https://example.com/ok">good link</a></p>',
      '<iframe src="javascript:window.__pwned = 1"></iframe>',
      '<svg onload="window.__pwned = 1"><circle r="1"/></svg>',
      '<style>body { display: none }</style>',
    ].join(''),
  )

  const body = editorBody(page)
  await expect(body).toContainText('kept bold')
  await expect(body).toContainText('bad link')
  await expect(body.locator('a[href="https://example.com/ok"]')).toHaveText('good link')
  await expect(body.locator('script, img, iframe, svg, style')).toHaveCount(0)
  await expect(body.locator('a[href^="javascript"]')).toHaveCount(0)
  await expect(body.locator('[onclick], [onerror], [onload]')).toHaveCount(0)
  expect(
    await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned),
  ).toBeUndefined()
  await expect(page.locator('body')).toBeVisible()
})

test('the title is its own field: Enter moves to the editor, and the sidebar and a reload show the new title', async ({
  page,
}, testInfo) => {
  const original = unique(testInfo.project.name, 'Original')
  const renamed = unique(testInfo.project.name, 'Renamed')
  const id = await openNewPage(page, original)
  const field = page.getByTestId('page-title')
  await expect(field).toHaveValue(original)

  await field.fill(renamed)
  await field.press('Enter')

  await expect(editorBody(page)).toBeFocused()
  await expect(field).toHaveValue(renamed)
  await openSidebar(page)
  await expect(rowFor(page, renamed)).toBeVisible()

  await page.goto(`/p/${id}`)
  await expect(page.getByTestId('page-title')).toHaveValue(renamed)
})

test('a title with a line break in it becomes one line', async ({ page }, testInfo) => {
  await openNewPage(page, unique(testInfo.project.name, 'Lines'))
  const field = page.getByTestId('page-title')
  await field.fill('first\nsecond')
  await expect(field).toHaveValue('first second')
})
