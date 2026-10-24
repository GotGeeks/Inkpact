import { test, expect, type BrowserContext, type Locator, type Page, type WebSocketRoute } from '@playwright/test'
import { ServerProcess } from './server.ts'
import { E2E_SERVER_PORT } from '../playwright.config.ts'

const server = new ServerProcess(E2E_SERVER_PORT)

test.beforeAll(() => server.start())
test.afterAll(() => server.stop())

interface Client {
  context: BrowserContext
  page: Page
  editor: Locator
  /** Cuts the WebSocket and refuses reconnects (true), or lets the client reconnect (false). */
  setOffline(offline: boolean): void
}

let run = 0
const newPageId = () => `e2e-${Date.now().toString(36)}-${run++}`

/** A separate browser context per client, like two different browsers. */
async function openClient(browser: import('@playwright/test').Browser, pageId: string, user: string): Promise<Client> {
  const context = await browser.newContext()
  const page = await context.newPage()

  // context.setOffline does not close an open WebSocket in Chromium, so the socket goes through a
  // route that can be cut: the page talks to the route, the route talks to the real server.
  let offline = false
  const open: WebSocketRoute[] = []
  await page.routeWebSocket(/\/ws$/, (route) => {
    if (offline) {
      void route.close()
      return
    }
    route.connectToServer()
    open.push(route)
  })

  await page.goto(`/?page=${pageId}&user=${user}`)
  await expect(page.locator('#status')).toHaveText('connected')
  return {
    context,
    page,
    editor: page.locator('.ProseMirror'),
    setOffline(value) {
      offline = value
      if (value) for (const route of open.splice(0)) void route.close()
    },
  }
}

async function typeText(client: Client, text: string, delayMs = 15) {
  await client.editor.click()
  await client.page.keyboard.press('Control+End')
  await client.page.keyboard.type(text, { delay: delayMs })
}

/** Two clients typing into a new empty page each create their own first paragraph, so ignore line breaks. */
const flat = (text: string) => text.replaceAll('\n', '')

/** True when every character of `part` appears in `whole`, in order. */
function isSubsequence(part: string, whole: string) {
  let i = 0
  for (const char of whole) if (char === part[i]) i++
  return i === part.length
}

async function expectConverged(a: Client, b: Client, pieces: string[], timeout = 15_000) {
  const total = pieces.join('').length
  await expect
    .poll(
      async () => {
        const [textA, textB] = [await a.editor.innerText(), await b.editor.innerText()]
        const converged =
          textA === textB && flat(textA).length === total && pieces.every((p) => isSubsequence(p, flat(textA)))
        return converged ? 'converged' : `A=${JSON.stringify(textA)} B=${JSON.stringify(textB)}`
      },
      { timeout, message: `both editors should converge on all of: ${pieces.join(' | ')}` },
    )
    .toBe('converged')
}

test('two browsers typing at the same time converge with nothing lost', async ({ browser }) => {
  const pageId = newPageId()
  const [a, b] = [await openClient(browser, pageId, 'alice'), await openClient(browser, pageId, 'bob')]

  await Promise.all([typeText(a, 'alpha-alpha-alpha-alpha'), typeText(b, 'BRAVO-BRAVO-BRAVO-BRAVO')])

  await expectConverged(a, b, ['alpha-alpha-alpha-alpha', 'BRAVO-BRAVO-BRAVO-BRAVO'])
  await a.context.close()
  await b.context.close()
})

test('a browser that goes offline merges its edits when it comes back', async ({ browser }) => {
  const pageId = newPageId()
  const [a, b] = [await openClient(browser, pageId, 'alice'), await openClient(browser, pageId, 'bob')]

  await typeText(a, 'base ')
  await expect(b.editor).toHaveText('base')

  a.setOffline(true)
  await expect(a.page.locator('#status')).toHaveText('disconnected')

  await typeText(a, 'offline-A ')
  await typeText(b, 'online-B ')
  // The other side must not see the offline edit yet.
  await expect(b.editor).not.toContainText('offline-A')

  a.setOffline(false)

  await expectConverged(a, b, ['base ', 'offline-A ', 'online-B '])
  await a.context.close()
  await b.context.close()
})

test('a server restart mid-edit loses nothing, and a fresh browser rebuilds the page from Postgres', async ({
  browser,
}) => {
  const pageId = newPageId()
  const [a, b] = [await openClient(browser, pageId, 'alice'), await openClient(browser, pageId, 'bob')]

  await typeText(a, 'before ')
  await expect(b.editor).toHaveText('before')

  await server.stop()
  await expect(a.page.locator('#status')).toHaveText('disconnected')
  await expect(b.page.locator('#status')).toHaveText('disconnected')

  // Both keep typing while the server is down; neither can see the other.
  await typeText(a, 'down-A ')
  await typeText(b, 'down-B ')
  await expect(b.editor).not.toContainText('down-A')

  await server.start()
  await expectConverged(a, b, ['before ', 'down-A ', 'down-B '], 30_000)

  const fresh = await openClient(browser, pageId, 'carol')
  await expectConverged(a, fresh, ['before ', 'down-A ', 'down-B '])

  await a.context.close()
  await b.context.close()
  await fresh.context.close()
})
