import type { Page, WebSocketRoute } from '@playwright/test'
import { expect, test } from './fixtures'
import { ADMIN, SIGNED_OUT } from './credentials'
import { createPageNamed, openSidebar, pageIdOf, signInThroughTheForm, signOut } from './helpers'

/**
 * The sync client in a real browser against the real server, under the strict policy: the page shows what the
 * connection is doing, and finds its way back by itself. (The sockets of `sync.spec.ts` are written by hand; these
 * are the app's own.)
 */

interface SocketControl {
  /** Cuts the socket and refuses new ones (true), or lets the client reconnect (false). */
  setOffline(offline: boolean): void
  /** The current socket stays open but nothing passes any more, and no close event fires: a phone that lost its Wi-Fi. */
  blackhole(): void
}

/** The app's socket goes through a route, which can cut it; context.setOffline does not close an open socket. */
async function controlSocket(page: Page): Promise<SocketControl> {
  let offline = false
  const open: { route: WebSocketRoute; server: WebSocketRoute }[] = []
  await page.routeWebSocket(/\/api\/sync$/, (route) => {
    if (offline) {
      void route.close()
      return
    }
    open.push({ route, server: route.connectToServer() })
  })
  return {
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

const pill = (page: Page) => page.getByTestId('sync-status')

async function openNewPage(page: Page, title: string) {
  await page.goto('/')
  const id = await pageIdOf(await createPageNamed(page, title))
  await expect(pill(page)).toHaveAttribute('data-state', 'live')
  return id
}

test('opening a page shows that it is live', async ({ page }, testInfo) => {
  await openNewPage(page, `Live-${testInfo.project.name}-${Date.now().toString(36)}`)
  await expect(pill(page)).toHaveText('Live')
})

test('a cut connection shows offline, and the page finds its way back', async ({
  page,
}, testInfo) => {
  const sockets = await controlSocket(page)
  await openNewPage(page, `Cut-${testInfo.project.name}-${Date.now().toString(36)}`)

  sockets.setOffline(true)
  await expect(pill(page)).toHaveAttribute('data-state', 'offline')
  await expect(pill(page)).toContainText('Offline')

  sockets.setOffline(false)
  await expect(pill(page)).toHaveAttribute('data-state', 'live', { timeout: 15_000 })
})

test('a connection that dies silently is noticed within about ten seconds', async ({
  page,
}, testInfo) => {
  const sockets = await controlSocket(page)
  await openNewPage(page, `Silent-${testInfo.project.name}-${Date.now().toString(36)}`)

  sockets.blackhole()
  await expect(pill(page)).toHaveAttribute('data-state', 'offline', { timeout: 15_000 })
  // New connections work again, so the client comes back by itself.
  await expect(pill(page)).toHaveAttribute('data-state', 'live', { timeout: 15_000 })
})

test('the browser going offline and online drops and restores the connection at once', async ({
  page,
  context,
}, testInfo) => {
  await openNewPage(page, `Events-${testInfo.project.name}-${Date.now().toString(36)}`)

  await context.setOffline(true)
  await expect(pill(page)).toHaveAttribute('data-state', 'offline', { timeout: 2_000 })

  await context.setOffline(false)
  await expect(pill(page)).toHaveAttribute('data-state', 'live', { timeout: 3_000 })
})

test('a page that does not exist says so', async ({ page }) => {
  await page.goto('/p/00000000-0000-4000-8000-000000000000')
  await expect(
    page.getByRole('heading', { level: 1, name: 'This page could not be found' }),
  ).toBeVisible()
})

test.describe('a session that ends', () => {
  // Its own sign-in, so ending it does not touch the session the other tests share.
  test('sends the open page to the login page, and back to the page after signing in', async ({
    browser,
    baseURL,
  }, testInfo) => {
    test.skip(testInfo.project.name !== 'desktop', 'The session logic is the same on every device')
    const context = await browser.newContext({ baseURL, storageState: SIGNED_OUT })
    const first = await context.newPage()
    await signInThroughTheForm(first)
    await expect(
      first.getByRole('heading', { level: 1, name: 'Welcome', exact: true }),
    ).toBeVisible()
    const id = await pageIdOf(await createPageNamed(first, `Ended-${Date.now().toString(36)}`))
    await expect(pill(first)).toHaveAttribute('data-state', 'live')

    // The same browser signs out in another tab: the session ends, and the first tab learns it from its socket.
    const second = await context.newPage()
    await second.goto('/')
    await signOut(second)

    await expect(first).toHaveURL(
      (url) => url.pathname === '/login' && url.searchParams.get('redirect') === `/p/${id}`,
    )

    await first.getByLabel('Email').fill(ADMIN.email)
    await first.getByLabel('Password').fill(ADMIN.password)
    await first.getByRole('button', { name: 'Sign in' }).click()
    await expect(first).toHaveURL((url) => url.pathname === `/p/${id}`)
    await openSidebar(first)
    await context.close()
  })
})
