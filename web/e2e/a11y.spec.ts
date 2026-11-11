import AxeBuilder from '@axe-core/playwright'
import type { Page, TestInfo } from '@playwright/test'
import { expect, test } from './fixtures'
import {
  closeDrawer,
  createPageNamed,
  openSidebar,
  openUserMenu,
  pageIdOf,
  rowFor,
} from './helpers'
import { importJex, openImportPage } from './importHelpers'
import { generateJex, generateLinkedJex } from './jex'

/**
 * Every screen and state, on every device (desktop, Pixel 7, iPhone 15 and a 320-pixel phone): no WCAG 2.2 A or AA
 * violation by axe, no sideways scrolling, and on a touch screen no control smaller than 44 pixels. A screen that is
 * added later is added here. A rule is never switched off for the whole file: an exception is written on the one element,
 * with the reason beside it, in `EXCEPTIONS`.
 */
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

/**
 * What axe reports and is meant. Each is one rule on one element (found by a piece of its HTML), with the reason. Empty
 * is the goal; every line here is a promise to look at it again.
 */
const EXCEPTIONS: { rule: string; html: string; reason: string }[] = [
  {
    rule: 'scrollable-region-focusable',
    html: 'data-testid="page-link-menu"',
    reason:
      'The same as the slash menu: the editor keeps the focus while the menu is open, and the keyboard reaches every page through aria-activedescendant, which scrolls the active one into view.',
  },
  {
    rule: 'scrollable-region-focusable',
    html: 'data-testid="slash-menu"',
    reason:
      'The editor keeps the focus while the menu is open, and the keyboard reaches every option through aria-activedescendant, which scrolls the active one into view. A focusable menu would take the focus out of the text.',
  },
]

const unique = (testInfo: TestInfo, name: string) =>
  `${name} ${testInfo.project.name} ${Date.now().toString(36)}`

const isTouch = (testInfo: TestInfo) => testInfo.project.name !== 'desktop'

async function axe(page: Page, name: string) {
  // A modal menu or dialog (Reka UI) hides the rest of the app from assistive technology and keeps the focus inside itself,
  // so the controls behind it, still in the tab order, cannot be reached while it is open. Axe cannot tell, and says so.
  const modalOpen = (await page.locator('[data-aria-hidden="true"]').count()) > 0
  let builder = new AxeBuilder({ page }).withTags(TAGS)
  if (modalOpen) builder = builder.disableRules(['aria-hidden-focus'])
  const { violations } = await builder.analyze()
  const left = violations
    .map((v) => ({
      ...v,
      nodes: v.nodes.filter(
        (n) => !EXCEPTIONS.some((e) => e.rule === v.id && n.html.includes(e.html)),
      ),
    }))
    .filter((v) => v.nodes.length > 0)
  expect(
    left.map(
      (v) =>
        `${name}: ${v.id} (${v.impact}) ${v.help}\n` +
        v.nodes.map((n) => `    ${n.target.join(' ')}\n    ${n.failureSummary}`).join('\n'),
    ),
  ).toEqual([])
}

/** Nothing wider than the screen: the page, and the area the pages are in. */
async function noSidewaysScroll(page: Page, name: string) {
  const wide = await page.evaluate(() => {
    const over = (el: Element | null) => (el ? el.scrollWidth - el.clientWidth : 0)
    return {
      page: over(document.scrollingElement),
      main: over(document.querySelector('main')),
    }
  })
  expect(wide, `${name}: sideways scrolling`).toEqual({ page: 0, main: 0 })
}

/** On a touch screen, the controls are at least 44 by 44 pixels, but a link inside a text and the editor's own content. */
async function targets(page: Page, name: string) {
  const small = await page.evaluate(() => {
    const controls = document.querySelectorAll(
      'button, a[href], input:not([type=hidden]), select, textarea, [role=button], [role=menuitem], [role=menuitemradio], [role=option], [role=treeitem], [role=tab]',
    )
    const found: string[] = []
    for (const el of controls) {
      if (el.closest('.ProseMirror, [data-testid="notice-copy"]')) continue
      const rect = el.getBoundingClientRect()
      // Not on the screen (hidden, or the skip link while it is not focused).
      if (rect.width <= 1 || rect.height <= 1) continue
      const style = getComputedStyle(el)
      if (style.visibility === 'hidden' || style.display === 'none') continue
      // A link in running text is exempt (WCAG 2.5.8): its line is its size.
      if (el.matches('a') && el.closest('p, li')?.textContent !== el.textContent) continue
      if (rect.width < 43.5 || rect.height < 43.5) {
        const label = el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 30) ?? ''
        found.push(
          `${el.tagName.toLowerCase()} "${label}" ${Math.round(rect.width)}x${Math.round(rect.height)}`,
        )
      }
    }
    return found
  })
  expect(small, `${name}: small targets`).toEqual([])
}

/** Axe, reflow and (on a touch screen) target sizes of whatever is showing. Axe runs in both themes: it checks the contrast. */
async function scan(page: Page, testInfo: TestInfo, name: string) {
  for (const colorScheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme })
    await axe(page, `${name} (${colorScheme})`)
  }
  await noSidewaysScroll(page, name)
  if (isTouch(testInfo)) await targets(page, name)
}

async function openNewPage(page: Page, title: string) {
  await page.goto('/')
  const id = await pageIdOf(await createPageNamed(page, title))
  await closeDrawer(page)
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
  await expect(page.getByTestId('editor').locator('.ProseMirror')).toHaveAttribute(
    'contenteditable',
    'true',
  )
  return id
}

test.describe('signed out', () => {
  test.use({ storageState: { cookies: [], origins: [] } })

  test('the sign-in page, empty and with an error', async ({ page }, testInfo) => {
    await page.goto('/login')
    await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
    await scan(page, testInfo, 'sign in')

    await page.getByLabel('Email').fill('nobody@example.com')
    await page.getByLabel('Password').fill('not the password at all')
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('alert')).toBeVisible()
    await scan(page, testInfo, 'sign in with an error')
  })
})

test('the start page', async ({ page }, testInfo) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome', exact: true })).toBeVisible()
  await scan(page, testInfo, 'start page')
})

test('a cached page remains accessible while offline', async ({ page }, testInfo) => {
  await openNewPage(page, unique(testInfo, 'Offline accessibility'))
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-local', 'saved')
  await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined))
  await page.reload()
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
  await page.context().setOffline(true)
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'offline')
  await scan(page, testInfo, 'offline cached page')
  await page.context().setOffline(false)
})

test('the sidebar with favorites, and the start page with recently opened pages', async ({
  page,
}, testInfo) => {
  const title = unique(testInfo, 'Starred')
  await page.goto('/')
  const row = await createPageNamed(page, title)
  const id = await pageIdOf(row)
  await closeDrawer(page)
  await expect(page.getByTestId('page-title')).toHaveValue(title)
  await page.getByRole('button', { name: 'Favorite', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Favorite', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  )
  await scan(page, testInfo, 'page with a star')

  await openSidebar(page)
  await expect(
    page.getByRole('navigation', { name: 'Favorites' }).getByRole('link', { name: title }),
  ).toBeVisible()
  await scan(page, testInfo, 'sidebar with favorites')
  await closeDrawer(page)

  // The visit is recorded when the page opens; the start page lists it once the server has it.
  await expect
    .poll(async () => {
      const workspaces = (await (await page.request.get('/api/workspaces')).json()) as {
        id: string
      }[]
      const recent = (await (
        await page.request.get(`/api/workspaces/${workspaces[0]!.id}/recent`)
      ).json()) as { pageId: string }[]
      return recent.some((r) => r.pageId === id)
    })
    .toBe(true)
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 2, name: 'Recently opened' })).toBeVisible()
  await scan(page, testInfo, 'start page with recently opened pages')
})

test('the sidebar: on a phone the drawer, the tree and a row menu', async ({ page }, testInfo) => {
  const title = unique(testInfo, 'Tree')
  await page.goto('/')
  const row = await createPageNamed(page, title)
  await openSidebar(page)
  await scan(page, testInfo, 'sidebar')

  await row.getByRole('button', { name: /^Actions for / }).click()
  await expect(page.getByRole('menu')).toBeVisible()
  await scan(page, testInfo, 'row menu')
})

test('a deep branch of the tree keeps room for its titles, on every screen', async ({
  page,
}, testInfo) => {
  // Made through the API, as tree.spec does: five levels under a page of this test's own.
  const state = (await (await page.request.get('/api/auth/state')).json()) as { csrfToken: string }
  const workspaces = (await (await page.request.get('/api/workspaces')).json()) as { id: string }[]
  let parentId: string | null = null
  let deepest = ''
  for (let level = 1; level <= 5; level++) {
    const response = await page.request.post(`/api/workspaces/${workspaces[0]!.id}/pages`, {
      headers: { 'X-CSRF-Token': state.csrfToken },
      data: { title: unique(testInfo, `Level ${level}`), parentId },
    })
    expect(response.status()).toBe(201)
    const created: { id: string } = ((await response.json()) as { page: { id: string } }).page
    parentId = created.id
    deepest = created.id
  }

  // Opening the deepest page opens the branches above it.
  await page.goto(`/p/${deepest}`)
  await openSidebar(page)
  const row = page.locator(`[role="treeitem"][data-page-id="${deepest}"]`)
  await expect(row).toBeVisible()
  await scan(page, testInfo, 'deep tree')
  // The title keeps at least a third of the drawer (or sidebar) even five levels down.
  const [title, rowWidth] = await row.evaluate((el) => [
    el.querySelector('span.truncate')?.getBoundingClientRect().width ?? 0,
    el.getBoundingClientRect().width,
  ])
  expect(title, 'room for the title').toBeGreaterThanOrEqual(rowWidth / 3)
})

test('a page with every kind of block', async ({ page }, testInfo) => {
  await openNewPage(page, unique(testInfo, 'Blocks'))
  const body = page.getByTestId('editor').locator('.ProseMirror')
  await body.click()
  for (const query of ['h1', 'ul', 'todo', 'quo', 'snippet', 'grid', 'hr']) {
    await page.keyboard.type(`/${query}`)
    await expect(page.getByTestId('slash-menu')).toBeVisible()
    await page.keyboard.press('Enter')
    await expect(page.getByTestId('slash-menu')).toBeHidden()
    await page.keyboard.type('some text')
    await page.keyboard.press('Control+End')
  }
  await expect(body.locator('table')).toBeAttached()
  await scan(page, testInfo, 'page with blocks')
})

test('the slash menu', async ({ page }, testInfo) => {
  await openNewPage(page, unique(testInfo, 'Slash'))
  await page.getByTestId('editor').locator('.ProseMirror').click()
  await page.keyboard.type('/')
  await expect(page.getByTestId('slash-menu')).toBeVisible()
  await scan(page, testInfo, 'slash menu')
  await page.keyboard.type('zzz')
  await expect(page.getByTestId('slash-menu')).toContainText('No results')
  await scan(page, testInfo, 'slash menu without results')
})

test('the menu of pages, the link picker and the link menu', async ({ page }, testInfo) => {
  const target = unique(testInfo, 'Linked page')
  await openNewPage(page, target)
  await openNewPage(page, unique(testInfo, 'Links here'))
  const body = page.getByTestId('editor').locator('.ProseMirror')
  await body.click()

  // [[ opens the menu of pages at the caret.
  await page.keyboard.type('hello [[')
  await expect(page.getByTestId('page-link-menu')).toBeVisible()
  await expect(page.getByTestId('page-link-menu').getByRole('option').first()).toBeVisible()
  await scan(page, testInfo, 'menu of pages')
  await page.keyboard.type('zzzz')
  await expect(page.getByTestId('page-link-menu')).toContainText('No pages found')
  await scan(page, testInfo, 'menu of pages without results')
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('page-link-menu')).toBeHidden()
  for (let i = 0; i < '[[zzzz'.length; i++) await page.keyboard.press('Backspace')

  // Ctrl+K on selected text opens the picker, a dialog.
  await page.keyboard.type('world')
  for (let i = 0; i < 5; i++) await page.keyboard.press('Shift+ArrowLeft')
  await page.keyboard.press('ControlOrMeta+k')
  await expect(page.getByTestId('link-picker')).toBeVisible()
  await page.getByTestId('link-picker-input').fill(target.slice(-12))
  await expect(page.getByTestId('link-picker').getByRole('option').first()).toBeVisible()
  await scan(page, testInfo, 'link picker')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('link-picker')).toBeHidden()

  // The menu under a link, with change and remove.
  await body.getByRole('link', { name: 'world' }).click()
  await expect(page.getByTestId('link-menu')).toBeVisible()
  await expect(page.getByTestId('link-remove')).toBeVisible()
  await scan(page, testInfo, 'link menu')
})

test('the block menu (a mouse) and the toolbar (a touch screen)', async ({ page }, testInfo) => {
  await openNewPage(page, unique(testInfo, 'Menus'))
  const body = page.getByTestId('editor').locator('.ProseMirror')
  await body.click()
  await page.keyboard.type('some text')

  if (isTouch(testInfo)) {
    await expect(page.getByTestId('editor-toolbar')).toBeVisible()
    await scan(page, testInfo, 'toolbar')
  } else {
    await body.locator('p').first().hover()
    await page.getByTestId('block-handle').click()
    await expect(page.getByTestId('block-menu')).toBeVisible()
    await scan(page, testInfo, 'block menu')
  }
})

test('the table menu', async ({ page }, testInfo) => {
  await openNewPage(page, unique(testInfo, 'Table'))
  const body = page.getByTestId('editor').locator('.ProseMirror')
  await body.click()
  await page.keyboard.type('/grid')
  await page.keyboard.press('Enter')
  await expect(body.locator('table')).toBeAttached()
  await expect(page.getByTestId('table-menu')).toBeVisible()
  await scan(page, testInfo, 'table menu')
})

test('a page in the trash and the trash', async ({ page }, testInfo) => {
  const title = unique(testInfo, 'Trashed')
  await page.goto('/')
  const row = await createPageNamed(page, title)
  const id = await pageIdOf(row)
  await row.getByRole('button', { name: /^Actions for / }).click()
  await page.getByRole('menuitem', { name: 'Move to trash', exact: true }).click()

  // The notice with Undo, on top of the page.
  await expect(page.getByRole('button', { name: 'Undo' }).first()).toBeVisible()
  await scan(page, testInfo, 'notice')

  await page.goto(`/p/${id}`)
  await expect(page.getByRole('status').filter({ hasText: 'in the trash' })).toBeVisible()
  // The tab names the page, also in the trash, which the live tree does not hold.
  await expect(page).toHaveTitle(`${title} – Inkpact`)
  await scan(page, testInfo, 'page in the trash')

  await page.goto('/trash')
  await expect(page.getByTestId('trash-list')).toContainText(title)
  await scan(page, testInfo, 'trash')
})

test('a page that does not exist', async ({ page }, testInfo) => {
  await page.goto('/p/00000000-0000-0000-0000-000000000000')
  await expect(
    page.getByRole('heading', { level: 1, name: 'This page could not be found' }),
  ).toBeVisible()
  await scan(page, testInfo, 'page not found')
})

test('the search dialog, with results and without', async ({ page }, testInfo) => {
  const title = unique(testInfo, 'Searchable')
  await page.goto('/')
  await createPageNamed(page, title)
  await closeDrawer(page)

  const open = page.getByTestId('search-open')
  if (await open.isVisible()) await open.click()
  else await page.getByTestId('search-row').click()
  const input = page.getByRole('combobox', { name: 'Search pages' })
  await expect(input).toBeFocused()
  await input.fill(title.split(' ')[0]!)
  await expect(page.getByTestId('search-results').getByRole('option').first()).toBeVisible()
  await scan(page, testInfo, 'search with results')

  await input.fill('qqqqzzzzxxxx')
  await expect(page.getByTestId('search-dialog')).toContainText('Nothing found')
  await scan(page, testInfo, 'search without results')
})

test('the palette: pages, tags, commands, the shortcut help and a list that could not be read', async ({
  page,
}, testInfo) => {
  await openNewPage(page, unique(testInfo, 'Palette'))
  async function openPalette() {
    const open = page.getByTestId('search-open')
    if (await open.isVisible()) await open.click()
    else await page.getByTestId('search-row').click()
    await expect(page.getByTestId('search-dialog')).toBeVisible()
  }

  await openPalette()
  await expect(page.getByRole('group', { name: 'Recently opened' })).toBeVisible()
  await expect(page.getByTestId('search-results')).not.toHaveAttribute('aria-busy', 'true')
  await scan(page, testInfo, 'palette pages')

  await page.getByRole('tab', { name: 'Tags' }).click()
  await expect(page.locator('#palette-tags-results')).not.toHaveAttribute('aria-busy', 'true')
  await scan(page, testInfo, 'palette tags')

  await page.getByRole('tab', { name: 'Commands' }).click()
  await expect(page.locator('#palette-commands-results').getByRole('option').first()).toBeVisible()
  await scan(page, testInfo, 'palette commands')

  await page.getByTestId('palette-shortcut-help').click()
  await expect(page.getByRole('button', { name: 'Back to the palette' })).toBeVisible()
  await scan(page, testInfo, 'palette shortcut help')
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('search-dialog')).toBeHidden()

  await page.route('**/api/workspaces/*/favorites', (route) =>
    route.request().method() === 'GET'
      ? route.fulfill({
          status: 500,
          contentType: 'application/json',
          body: '{"code":"server_error"}',
        })
      : route.continue(),
  )
  await openPalette()
  await expect(
    page.getByRole('alert').filter({ hasText: 'Favorites could not be loaded.' }),
  ).toBeVisible()
  await scan(page, testInfo, 'palette with a list that could not be read')
})

test('the move dialog', async ({ page }, testInfo) => {
  const row = await (async () => {
    await page.goto('/')
    return createPageNamed(page, unique(testInfo, 'Move'))
  })()
  await row.getByRole('button', { name: /^Actions for / }).click()
  await page.getByRole('menuitem', { name: 'Move to…', exact: true }).click()
  await expect(page.getByRole('dialog', { name: /Move/ })).toBeVisible()
  await scan(page, testInfo, 'move dialog')
})

test('the account menu and the import page', async ({ page }, testInfo) => {
  await page.goto('/')
  await openUserMenu(page)
  await expect(page.getByRole('menu')).toBeVisible()
  await scan(page, testInfo, 'account menu')

  await page.getByRole('menuitem', { name: 'Import from Joplin', exact: true }).click()
  await expect(page.getByTestId('import-choose')).toBeVisible()
  await scan(page, testInfo, 'import page')
})

test('a page with the pages that link to it, and a link to a page in the trash', async ({
  page,
}, testInfo) => {
  test.setTimeout(180_000)
  const jex = generateLinkedJex(unique(testInfo, 'Links').replaceAll(' ', '-'))
  await openImportPage(page)
  await importJex(page, jex)
  await openSidebar(page)
  await rowFor(page, jex.titles.notebook)
    .getByRole('button', { name: `Expand ${jex.titles.notebook}` })
    .click()
  await rowFor(page, jex.titles.second).click()
  await closeDrawer(page)
  await expect(page.getByTestId('page-title')).toHaveValue(jex.titles.second)

  // The server indexes the links when it compacts the page, a few seconds after the import (see playwright.config.ts).
  await expect(async () => {
    // Finish startup before reloading; aborting its fetches produces access-control errors in WebKit.
    await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
    await page.reload()
    await expect(page.getByTestId('backlinks')).toContainText(jex.titles.first, { timeout: 4_000 })
  }).toPass({ timeout: 90_000, intervals: [2_000] })
  await scan(page, testInfo, 'a page with backlinks')

  // The page it links to goes to the trash: its link is struck through, with the reason for a screen reader.
  await openSidebar(page)
  const row = rowFor(page, jex.titles.first)
  await row.getByRole('button', { name: /^Actions for / }).click()
  await page.getByRole('menuitem', { name: 'Move to trash', exact: true }).click()
  await closeDrawer(page)
  await expect(page.locator('[data-broken-link="trash"]')).toBeVisible()
  await scan(page, testInfo, 'a page with a link to a page in the trash')
})

test('tags: the row with the add field open, the Tags view and a tag’s own page', async ({
  page,
}, testInfo) => {
  test.setTimeout(180_000)
  const jex = generateJex(unique(testInfo, 'Tags').replaceAll(' ', '-'))
  await openImportPage(page)
  await importJex(page, jex)
  await openSidebar(page)
  // The notebook page itself, not a note: it has no attachment, which axe scans separately (editor-blocks.spec.ts).
  await rowFor(page, jex.titles.notebook).click()
  await closeDrawer(page)
  await expect(page.getByTestId('page-title')).toHaveValue(jex.titles.notebook)

  const tagName = unique(testInfo, 'label').replaceAll(' ', '-')
  await page.getByTestId('add-tag-button').click()
  await page.getByTestId('add-tag-field').fill(tagName)
  await scan(page, testInfo, 'the tag row with the add field open')
  await page.getByRole('option', { name: `Create tag “${tagName}”` }).click()
  await page.keyboard.press('Escape')

  await openSidebar(page)
  await page.getByRole('link', { name: 'Tags', exact: true }).click()
  await closeDrawer(page)
  await scan(page, testInfo, 'the Tags view')

  await page.getByRole('link', { name: tagName }).click()
  await scan(page, testInfo, 'a tag’s own page')
})

test('the same screens in Dutch have names too', async ({ page }, testInfo) => {
  await page.goto('/')
  await openUserMenu(page)
  await page.getByRole('menuitemradio', { name: 'Nederlands' }).click()
  await closeDrawer(page)
  await expect(page.getByRole('heading', { level: 1, name: 'Welkom', exact: true })).toBeVisible()
  await scan(page, testInfo, 'start page in Dutch')
  await openSidebar(page)
  await scan(page, testInfo, 'sidebar in Dutch')

  // Back to English, for whoever runs next on this account.
  await openUserMenu(page)
  await page.getByRole('menuitemradio', { name: 'English' }).click()
})
