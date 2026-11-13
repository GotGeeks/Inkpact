import type { Page, TestInfo } from '@playwright/test'
import { expect, test } from './fixtures'
import { closeDrawer, createPageNamed, pageIdOf } from './helpers'

/**
 * A page's history (run B.5b, the read-only panel): the panel lists a page's versions and shows one, and looking at a
 * version changes nothing on the page.
 *
 * The versions are answered here, not made by the server: the e2e server keeps the capture at its own pace (two idle
 * minutes, see playwright.config.ts), and a faster one made the rest of the suite time out. That versions are captured
 * and listed through the real hub and sidecar is a server test (`HistoryEndpointTests`, the worker test). Run B.5b's
 * restore makes versions at once, and brings a journey with real versions.
 */

const unique = (testInfo: TestInfo, name: string) =>
  `${name} ${testInfo.project.name} ${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`

const editorBody = (page: Page) => page.getByTestId('editor').locator('.ProseMirror')
const panel = (page: Page) => page.getByRole('dialog')
const rows = (page: Page) => panel(page).getByTestId('version-row')

async function newPage(page: Page, title: string) {
  await page.goto('/')
  const id = await pageIdOf(await createPageNamed(page, title))
  await closeDrawer(page)
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
  await expect(editorBody(page)).toHaveAttribute('contenteditable', 'true')
  return id
}

const doc = (text: string) => ({
  type: 'doc',
  content: [{ type: 'paragraph', content: [{ type: 'text', text }] }],
})

/**
 * Answers the page's list and its versions' content, as the server would: newest first, a rename on top. Intercepted
 * without a reload, which WebKit's service worker would otherwise answer past the route.
 */
async function answerVersions(page: Page, pageId: string, title: string) {
  const versions = [
    {
      id: 'v3',
      createdAt: '2026-11-12T12:00:00Z',
      kind: 'rename',
      title: `${title} renamed`,
      text: 'The first words, and more of them',
      by: true,
    },
    {
      id: 'v2',
      createdAt: '2026-11-12T11:00:00Z',
      kind: 'automatic',
      title,
      text: 'The first words, and more of them',
      by: false,
    },
    {
      id: 'v1',
      createdAt: '2026-11-12T10:00:00Z',
      kind: 'automatic',
      title,
      text: 'The first words',
      by: false,
    },
  ].map((v) => ({
    ...v,
    id: `0a1b2c3d-0000-4000-8000-00000000000${v.id.slice(1)}`,
    createdBy: v.by ? { id: '0a1b2c3d-0000-4000-8000-0000000000aa', displayName: 'Someone' } : null,
    bytes: 100,
  }))
  await page.route(
    (url) => url.pathname === `/api/pages/${pageId}/versions`,
    (route) =>
      route.fulfill({
        json: {
          versions: versions.map(({ id, createdAt, kind, title, createdBy, bytes }) => ({
            id,
            createdAt,
            kind,
            title,
            createdBy,
            bytes,
          })),
          next: null,
          state: 'ready',
          failure: null,
        },
      }),
  )
  await page.route(
    (url) => url.pathname.startsWith(`/api/pages/${pageId}/versions/`),
    (route) => {
      const version = versions.find((v) => route.request().url().endsWith(v.id))!
      return route.fulfill({
        json: {
          id: version.id,
          createdAt: version.createdAt,
          kind: version.kind,
          title: version.title,
          content: doc(version.text),
        },
      })
    },
  )
}

async function closePanel(page: Page) {
  await panel(page).getByRole('button', { name: 'Close the history' }).click()
  await expect(panel(page)).toBeHidden()
}

test("a page's versions are listed and one is looked at, without changing the page", async ({
  page,
  isMobile,
}, testInfo) => {
  const title = unique(testInfo, 'History')
  const pageId = await newPage(page, title)
  await editorBody(page).click()
  await page.keyboard.type('What the page says now')

  // The real server answers for the page, in the shape the panel reads (asked past the routes below).
  const real = await page.request.get(`/api/pages/${pageId}/versions`)
  expect(real.status()).toBe(200)
  const body = (await real.json()) as { versions: unknown[]; state: string }
  expect(Array.isArray(body.versions)).toBe(true)
  expect(['ready', 'pending']).toContain(body.state)

  await answerVersions(page, pageId, title)
  const open = page.getByRole('button', { name: 'Page history' })
  await open.click()
  await expect(panel(page)).toBeVisible()
  await expect(panel(page).getByRole('heading', { name: `History of “${title}”` })).toBeVisible()
  await expect(rows(page)).toHaveCount(3)
  await expect(rows(page).nth(0)).toContainText('Renamed · Someone')
  await expect(rows(page).nth(0)).toContainText(`${title} renamed`)
  await expect(rows(page).nth(2)).toContainText('Saved automatically')

  // The oldest: the first words alone, under the first title.
  await rows(page).nth(2).click()
  const preview = panel(page).getByTestId('version-preview')
  await expect(preview.getByTestId('version-title')).toHaveText(title)
  const content = preview.getByTestId('version-content').locator('.ProseMirror')
  await expect(content).toHaveText('The first words')
  await expect(content).toHaveAttribute('contenteditable', 'false')
  if (isMobile) {
    // On a phone the version takes the list's place, and the way back is a button.
    await expect(rows(page).first()).toBeHidden()
    await panel(page).getByRole('button', { name: 'Back to the versions' }).click()
    await expect(rows(page).nth(2)).toBeFocused()
  }

  // Closed: the focus is back on the button, and the page is as it was.
  await closePanel(page)
  await expect(open).toBeFocused()
  await expect(editorBody(page)).toHaveText('What the page says now')
  await expect(page.getByRole('textbox', { name: 'Title', exact: true })).toHaveValue(title)
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
})

test('a page without versions says so, and the history closes when another page is opened', async ({
  page,
}, testInfo) => {
  const otherId = await newPage(page, unique(testInfo, 'Other page'))
  // Made from inside the app, so that the browser's back button goes to the other page and the page view stays.
  const pageId = await pageIdOf(await createPageNamed(page, unique(testInfo, 'Without history')))
  await closeDrawer(page)
  await expect(editorBody(page)).toHaveAttribute('contenteditable', 'true')
  // Answered here: the e2e server keeps a version of a new page (its naming) within seconds, so a real page is not
  // without versions for long enough to look at.
  await page.route(
    (url) => url.pathname === `/api/pages/${pageId}/versions`,
    (route) => route.fulfill({ json: { versions: [], next: null, state: 'ready', failure: null } }),
  )

  await page.getByRole('button', { name: 'Page history' }).click()
  await expect(panel(page).getByTestId('history-empty')).toHaveText(
    'No versions yet. A version is kept a few minutes after the page changes.',
  )
  await page.keyboard.press('Escape')
  await expect(panel(page)).toBeHidden()

  await page.getByRole('button', { name: 'Page history' }).click()
  await expect(panel(page)).toBeVisible()
  // Back to the other page, as the browser's back button does.
  await page.goBack()
  await expect(page).toHaveURL(new RegExp(`/p/${otherId}$`))
  await expect(panel(page)).toBeHidden()
})
