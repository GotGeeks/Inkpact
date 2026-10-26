import type {
  Browser,
  BrowserContext,
  BrowserContextOptions,
  Locator,
  Page,
  TestInfo,
  WebSocketRoute,
} from '@playwright/test'
import { expect, test } from './fixtures'
import { closeDrawer, createPageNamed, pageIdOf } from './helpers'

/**
 * The spike's browser scenarios (1, 2, 4 and 5 of docs/spike-findings.md), against the real server and the real editor:
 * two browsers of the same person edit one page; one loses its connection in different ways; they must always end with
 * the same page that has every character in it.
 */

interface Client {
  context: BrowserContext
  page: Page
  editor: Locator
  status: Locator
  /** Cuts the socket and refuses new ones (true), or lets the client reconnect (false). */
  setOffline(offline: boolean): void
  /** The current socket stays open but nothing passes, and no close event fires: a phone that lost its Wi-Fi. */
  blackhole(): void
}

const unique = (project: string, name: string) => `${name}-${project}-${Date.now().toString(36)}`

/** A separate browser context per client, like two different browsers, signed in as the same person. */
async function openClient(
  browser: Browser,
  testInfo: TestInfo,
  baseURL: string | undefined,
  pageId: string,
): Promise<Client> {
  const context = await browser.newContext({
    ...(testInfo.project.use as BrowserContextOptions),
    baseURL,
  })
  const page = await context.newPage()

  // context.setOffline does not close an open WebSocket in Chromium, so the socket goes through a route that can be cut:
  // the page talks to the route, the route talks to the real server.
  let offline = false
  const open: { route: WebSocketRoute; server: WebSocketRoute }[] = []
  await page.routeWebSocket(/\/api\/sync$/, (route) => {
    if (offline) {
      void route.close()
      return
    }
    open.push({ route, server: route.connectToServer() })
  })

  await page.goto(`/p/${pageId}`)
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await expect(status).toHaveAttribute('data-state', 'live')
  await expect(editor).toHaveAttribute('contenteditable', 'true')
  return {
    context,
    page,
    editor,
    status,
    setOffline(value) {
      offline = value
      if (value) for (const { route } of open.splice(0)) void route.close()
    },
    blackhole() {
      for (const { route, server } of open.splice(0)) {
        route.onMessage(() => {})
        server.onMessage(() => {})
      }
    },
  }
}

/** Makes a page and returns its id, as the first client of every scenario. */
async function newPage(page: Page, title: string): Promise<string> {
  await page.goto('/')
  const id = await pageIdOf(await createPageNamed(page, title))
  await closeDrawer(page)
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
  return id
}

async function typeText(client: Client, text: string, delayMs = 15) {
  await client.editor.click()
  await client.page.keyboard.press('Control+End')
  await client.page.keyboard.type(text, { delay: delayMs })
}

/** True when every character of `part` appears in `whole`, in order. */
function isSubsequence(part: string, whole: string) {
  let i = 0
  for (const char of whole) if (char === part[i]) i++
  return i === part.length
}

/**
 * Both editors have the same text, with every typed piece in it in order, and exactly one paragraph: two clients typing
 * into a new page used to make a paragraph each (run 0.3), which the first paragraph made at page creation prevents.
 */
async function expectConverged(a: Client, b: Client, pieces: string[], timeout = 15_000) {
  const total = pieces.join('').length
  await expect
    .poll(
      async () => {
        const [textA, textB] = [await a.editor.innerText(), await b.editor.innerText()]
        const paragraphs = [
          await a.editor.locator('p').count(),
          await b.editor.locator('p').count(),
        ]
        const converged =
          textA === textB &&
          textA.length === total &&
          paragraphs[0] === 1 &&
          paragraphs[1] === 1 &&
          pieces.every((piece) => isSubsequence(piece, textA))
        return converged
          ? 'converged'
          : `A=${JSON.stringify(textA)} B=${JSON.stringify(textB)} p=${paragraphs}`
      },
      { timeout, message: `both editors should converge on all of: ${pieces.join(' | ')}` },
    )
    .toBe('converged')
}

test('two browsers typing at the same time converge with nothing lost, in one paragraph', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Concurrent'))
  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]

  await Promise.all([
    typeText(a, 'alpha-alpha-alpha-alpha'),
    typeText(b, 'BRAVO-BRAVO-BRAVO-BRAVO'),
  ])

  const pieces = ['alpha-alpha-alpha-alpha', 'BRAVO-BRAVO-BRAVO-BRAVO']
  await expectConverged(a, b, pieces)

  // A third browser gets the same text from the server alone.
  const fresh = await openClient(browser, testInfo, baseURL, id)
  await expectConverged(a, fresh, pieces)
  for (const client of [a, b, fresh]) await client.context.close()
})

test('a browser that goes offline merges its edits when it comes back', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Offline'))
  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]
  await typeText(a, 'base')
  await expect(b.editor).toContainText('base')

  a.setOffline(true)
  await expect(a.status).toHaveAttribute('data-state', 'offline')
  await typeText(a, ' offline-A')
  await typeText(b, ' online-B')
  // The other side must not see the offline edit yet.
  await expect(b.editor).not.toContainText('offline-A')

  a.setOffline(false)

  await expectConverged(a, b, ['base', ' offline-A', ' online-B'])
  for (const client of [a, b]) await client.context.close()
})

test('a connection that dies silently (Wi-Fi lost on a phone) is noticed, and its edits merge after reconnecting', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Silent'))
  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]
  await typeText(a, 'base')
  await expect(b.editor).toContainText('base')

  // Nothing passes any more, but the browser is not told: the socket still looks open.
  a.blackhole()
  await typeText(a, ' typed-on-phone')
  await typeText(b, ' typed-on-laptop')
  await expect(b.editor).not.toContainText('typed-on-phone')

  // The client has to notice by itself, reconnect, and send what the server is missing.
  await expectConverged(a, b, ['base', ' typed-on-phone', ' typed-on-laptop'], 40_000)
  for (const client of [a, b]) await client.context.close()
})

test('the browser going offline and online drops the connection at once and reconnects without waiting', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Events'))
  const [a, b] = [
    await openClient(browser, testInfo, baseURL, id),
    await openClient(browser, testInfo, baseURL, id),
  ]
  await typeText(a, 'base')
  await expect(b.editor).toContainText('base')

  // The browser's offline mode fires the page's offline and online events; the socket is left alone.
  await a.context.setOffline(true)
  await expect(a.status).toHaveAttribute('data-state', 'offline', { timeout: 2_000 })
  await typeText(a, ' while-offline')
  await typeText(b, ' meanwhile')

  await a.context.setOffline(false)
  await expect(a.status).toHaveAttribute('data-state', 'live', { timeout: 3_000 })
  await expectConverged(a, b, ['base', ' while-offline', ' meanwhile'], 5_000)
  for (const client of [a, b]) await client.context.close()
})
