import type {
  Browser,
  BrowserContext,
  BrowserContextOptions,
  Locator,
  Page,
  TestInfo,
  WebSocketRoute,
} from '@playwright/test'
import { expect } from './fixtures'
import { closeDrawer, createPageNamed, pageIdOf } from './helpers'

/**
 * Two browsers of the same person on one page, for the specs that need a client to lose its connection: each client has
 * a context of its own, and its socket goes through a route that can be cut.
 */

export interface Client {
  context: BrowserContext
  page: Page
  editor: Locator
  status: Locator
  /** Cuts the socket and refuses new ones (true), or lets the client reconnect (false). */
  setOffline(offline: boolean): void
  /** The current socket stays open but nothing passes, and no close event fires: a phone that lost its Wi-Fi. */
  blackhole(): void
}

/** A separate browser context per client, like two different browsers, signed in as the same person. */
export async function openClient(
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
export async function newPage(page: Page, title: string): Promise<string> {
  await page.goto('/')
  const id = await pageIdOf(await createPageNamed(page, title))
  await closeDrawer(page)
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')
  return id
}

export async function typeText(client: Client, text: string, delayMs = 15) {
  await client.editor.click()
  await client.page.keyboard.press('Control+End')
  await client.page.keyboard.type(text, { delay: delayMs })
}
