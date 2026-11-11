import type { BrowserContextOptions, WebSocketRoute } from '@playwright/test'
import type { CachedPage } from '../src/offline/types'
import { ADMIN, SIGNED_OUT } from './credentials'
import { expect, test } from './fixtures'
import { signInThroughTheForm, signOut } from './helpers'
import { newPage, openClient, removeLastBlock } from './syncClients'

const unique = (project: string, name: string) => `${name}-${project}-${Date.now().toString(36)}`

test('a cached page survives an offline reload and converges exactly once', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Offline reload'))
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await expect(status).toHaveAttribute('data-local', 'saved')
  await expect(status).toHaveAttribute('data-unsent', 'false')
  expect(await page.evaluate(() => navigator.serviceWorker.ready.then(() => true))).toBe(true)
  await page.reload()
  await expect(status).toHaveAttribute('data-state', 'live')
  await expect(status).toHaveAttribute('data-local', 'saved')

  await page.context().setOffline(true)
  await expect(status).toHaveAttribute('data-state', 'offline')
  await editor.click()
  await page.keyboard.type('offline-once')
  await expect(status).toHaveAttribute('data-unsent', 'true')
  await expect(status).toHaveAttribute('data-local', 'saved')
  await expect(status).toContainText('not yet synced')

  await page.reload()
  await expect(editor).toContainText('offline-once')
  await expect(editor).toHaveAttribute('contenteditable', 'true')
  await expect(status).toHaveAttribute('data-state', 'offline')
  await expect(status).toHaveAttribute('data-unsent', 'true')
  await expect(status).toHaveAttribute('data-local', 'saved')

  await page.context().setOffline(false)
  await expect(status).toHaveAttribute('data-state', 'live', { timeout: 15_000 })
  await expect(status).toHaveAttribute('data-unsent', 'false', { timeout: 15_000 })
  const other = await openClient(browser, testInfo, baseURL, id)
  await expect(other.editor).toHaveText('offline-once')
  await expect(other.editor.locator('p')).toHaveCount(1)
  await other.context.close()
})

test('a confirmed edit stays confirmed after an offline reload', async ({ page }, testInfo) => {
  await newPage(page, unique(testInfo.project.name, 'Confirmed reload'))
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined))
  await page.reload()
  await expect(status).toHaveAttribute('data-state', 'live')
  await editor.click()
  await page.keyboard.type('already-confirmed')
  await expect(status).toHaveAttribute('data-unsent', 'false')
  await expect(status).toHaveAttribute('data-local', 'saved')

  await page.context().setOffline(true)
  await expect(status).toHaveAttribute('data-state', 'offline')
  await page.reload()
  await expect(editor).toContainText('already-confirmed')
  await expect(status).toHaveAttribute('data-unsent', 'false')
  await expect(status).not.toContainText('not yet synced')
})

test('a confirmed cached page remains readable after access becomes read-only', async ({
  page,
}, testInfo) => {
  await newPage(page, unique(testInfo.project.name, 'Read-only confirmed page'))
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined))
  await page.reload()
  await expect(status).toHaveAttribute('data-state', 'live')
  await editor.click()
  await page.keyboard.type('confirmed before access changed')
  await expect(status).toHaveAttribute('data-unsent', 'false')
  await expect(status).toHaveAttribute('data-local', 'saved')

  await page.context().setOffline(true)
  await expect(status).toHaveAttribute('data-state', 'offline')
  await page.reload()
  await expect(status).toHaveAttribute('data-state', 'offline')
  await expect(status).toHaveAttribute('data-unsent', 'false')
  await page.evaluate(() => {
    const state = window as Window & { __permissionChecks?: number }
    state.__permissionChecks = 0
    const original = window.fetch.bind(window)
    window.fetch = async (input, init) => {
      const response = await original(input, init)
      const url = new URL(input instanceof Request ? input.url : String(input), location.href)
      if (url.pathname !== '/api/workspaces') return response
      state.__permissionChecks = (state.__permissionChecks ?? 0) + 1
      const workspaces: { role: string }[] = await response.json()
      return new Response(JSON.stringify(workspaces.map((item) => ({ ...item, role: 'viewer' }))), {
        status: response.status,
        statusText: response.statusText,
        headers: response.headers,
      })
    }
  })
  await page.context().setOffline(false)
  await expect
    .poll(
      () =>
        page.evaluate(
          () => (window as Window & { __permissionChecks?: number }).__permissionChecks ?? 0,
        ),
      { timeout: 15_000 },
    )
    .toBeGreaterThan(0)
  await expect(status).toHaveAttribute('data-state', 'live', { timeout: 15_000 })
  await expect(status).toHaveAttribute('data-local', 'saved')
  await expect(editor).toContainText('confirmed before access changed')
  await expect(editor).toHaveAttribute('contenteditable', 'false')
  await expect(page.getByText(/Your unsynced changes are still on this device/)).toHaveCount(0)

  await page.context().setOffline(true)
  await expect(status).toHaveAttribute('data-state', 'offline')
  await page.reload()
  await expect(editor).toContainText('confirmed before access changed')
  await expect(editor).toHaveAttribute('contenteditable', 'false')
})

test('the shell cache contains no API data and an uncached page reconnects without a reload', async ({
  page,
}, testInfo) => {
  await newPage(page, unique(testInfo.project.name, 'Cache boundary'))
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-local', 'saved')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await editor.click()
  await page.keyboard.type('note-content-never-in-shell-cache')
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-unsent', 'false')
  await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined))
  await page.reload()
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-state', 'live')

  const unknown = await page.evaluate(async () => {
    const response = await fetch('/api/this-route-does-not-exist')
    return {
      status: response.status,
      type: response.headers.get('content-type'),
      body: await response.json(),
    }
  })
  expect(unknown.status).toBe(404)
  expect(unknown.type).toContain('application/json')
  expect(unknown.body).toEqual({ code: 'not_found' })

  const manifest = await page.evaluate(async () => {
    const response = await fetch('/manifest.webmanifest')
    return response.json()
  })
  expect(manifest.name).toBe('Inkpact')
  expect(manifest.icons.map((icon: { sizes: string }) => icon.sizes)).toEqual(
    expect.arrayContaining(['192x192', '512x512']),
  )
  if (testInfo.project.name === 'desktop') {
    const protocol = await page.context().newCDPSession(page)
    const { installabilityErrors } = await protocol.send('Page.getInstallabilityErrors')
    expect(installabilityErrors).toEqual([])
    await protocol.detach()
  }

  const cached = await page.evaluate(async () => {
    const names = await caches.keys()
    return Promise.all(
      names
        .filter((name) => name.startsWith('inkpact-shell-'))
        .map(async (name) => {
          const cache = await caches.open(name)
          return Promise.all(
            (await cache.keys()).map(async (request) => ({
              path: new URL(request.url).pathname,
              containsNote: (await (await cache.match(request))!.text()).includes(
                'note-content-never-in-shell-cache',
              ),
            })),
          )
        }),
    )
  })
  expect(cached.length).toBeGreaterThan(0)
  expect(cached.flat().map((entry) => entry.path)).toContain('/index.html')
  expect(cached.flat().some((entry) => entry.path.startsWith('/assets/'))).toBe(true)
  expect(
    cached.flat().some((entry) => entry.path === '/api' || entry.path.startsWith('/api/')),
  ).toBe(false)
  expect(cached.flat().some((entry) => entry.containsNote)).toBe(false)

  const retryId = await newPage(page, unique(testInfo.project.name, 'Offline retry'))
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-local', 'saved')
  await page.evaluate(async () => {
    const request = indexedDB.open('inkpact-offline')
    const database = await new Promise<IDBDatabase>((resolve, reject) => {
      request.onsuccess = () => resolve(request.result)
      request.onerror = () => reject(request.error)
    })
    const transaction = database.transaction(['pages', 'snapshots'], 'readwrite')
    transaction.objectStore('pages').clear()
    transaction.objectStore('snapshots').clear()
    await new Promise<void>((resolve, reject) => {
      transaction.oncomplete = () => resolve()
      transaction.onabort = () => reject(transaction.error)
      transaction.onerror = () => reject(transaction.error)
    })
    const verification = database.transaction(['pages', 'snapshots'], 'readonly')
    const pages = verification.objectStore('pages').count()
    const snapshots = verification.objectStore('snapshots').count()
    const counts = await new Promise<[number, number]>((resolve, reject) => {
      verification.oncomplete = () => resolve([pages.result, snapshots.result])
      verification.onabort = () => reject(verification.error)
      verification.onerror = () => reject(verification.error)
    })
    if (counts[0] !== 0 || counts[1] !== 0) throw new Error('Offline page data was not cleared')
    database.close()
  })

  await page.context().setOffline(true)
  await page.goto(`/p/${retryId}`)
  await expect(page.getByRole('heading', { name: 'Not available offline' })).toBeVisible()
  await expect(page.getByTestId('editor')).toHaveCount(0)
  const apiReached = await page.evaluate(() =>
    fetch('/api/auth/state').then(
      () => true,
      () => false,
    ),
  )
  expect(apiReached).toBe(false)
  await page.getByRole('button', { name: 'Try again' }).click()
  await expect(page.getByRole('alert')).toContainText('The server is still unavailable')
  await page.context().setOffline(false)
  await expect(page.getByTestId('editor')).toBeVisible({ timeout: 15_000 })
  await expect(page.getByTestId('page-title')).toHaveValue(/Offline retry/)
})

test('the first uncontrolled tab keeps old shell caches during initial worker activation', async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'The first-visit lifecycle is checked on desktop.')
  await page.addInitScript(() => {
    const container = navigator.serviceWorker
    const state = window as Window & {
      __registerWorker?: ServiceWorkerContainer['register']
    }
    state.__registerWorker = container.register.bind(container)
    Object.defineProperty(container, 'register', {
      configurable: true,
      value: () => new Promise<ServiceWorkerRegistration>(() => {}),
    })
  })
  await newPage(page, unique(testInfo.project.name, 'First visit'))
  await page.evaluate(() => caches.open('inkpact-shell-first-visit-old'))
  await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(false)
  await page.evaluate(async () => {
    const state = window as Window & {
      __registerWorker?: ServiceWorkerContainer['register']
    }
    Object.defineProperty(navigator.serviceWorker, 'register', {
      configurable: true,
      value: state.__registerWorker,
    })
    await navigator.serviceWorker.register(`/service-worker.js?first-visit=${Date.now()}`)
  })
  await expect
    .poll(() =>
      page.evaluate(
        async () => (await navigator.serviceWorker.getRegistration())?.active?.scriptURL,
      ),
    )
    .toContain('first-visit=')
  expect(await page.evaluate(() => caches.has('inkpact-shell-first-visit-old'))).toBe(true)
})

test('two offline tabs keep their own edits across reloads and merge on reconnect', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Two offline tabs'))
  const second = await page.context().newPage()
  await second.goto(`/p/${id}`)
  const statusA = page.getByTestId('sync-status')
  const statusB = second.getByTestId('sync-status')
  const editorA = page.getByTestId('editor').locator('.ProseMirror')
  const editorB = second.getByTestId('editor').locator('.ProseMirror')
  await expect(statusA).toHaveAttribute('data-local', 'saved')
  await expect(statusB).toHaveAttribute('data-local', 'saved')
  await page.context().setOffline(true)
  await expect(statusA).toHaveAttribute('data-state', 'offline')
  await expect(statusB).toHaveAttribute('data-state', 'offline')

  await editorA.click()
  await page.keyboard.type('tab-alpha')
  await editorB.click()
  await second.keyboard.type('tab-beta')
  await expect(statusA).toHaveAttribute('data-local', 'saved')
  await expect(statusB).toHaveAttribute('data-local', 'saved')
  await page.reload()
  await second.reload()
  await expect(editorA).toContainText('tab-alpha')
  await expect(editorA).toContainText('tab-beta')
  await expect(editorB).toContainText('tab-alpha')
  await expect(editorB).toContainText('tab-beta')
  const writerCount = await page.evaluate(async (pageId) => {
    const request = indexedDB.open('inkpact-offline')
    const database = await new Promise<IDBDatabase>((resolve, reject) => {
      request.onsuccess = () => resolve(request.result)
      request.onerror = () => reject(request.error)
    })
    const transaction = database.transaction('snapshots', 'readonly')
    const snapshots = transaction.objectStore('snapshots').getAll()
    const count = await new Promise<number>((resolve, reject) => {
      transaction.oncomplete = () =>
        resolve(snapshots.result.filter((snapshot) => snapshot.pageId === pageId).length)
      transaction.onabort = () => reject(transaction.error)
    })
    database.close()
    return count
  }, id)
  expect(writerCount).toBeLessThanOrEqual(2)

  await page.context().setOffline(false)
  await expect(statusA).toHaveAttribute('data-unsent', 'false', { timeout: 15_000 })
  await expect(statusB).toHaveAttribute('data-unsent', 'false', { timeout: 15_000 })
  const other = await openClient(browser, testInfo, baseURL, id)
  const text = await other.editor.innerText()
  expect(text.match(/tab-alpha/g)).toHaveLength(1)
  expect(text.match(/tab-beta/g)).toHaveLength(1)
  await other.context.close()
  await second.close()
})

test('clean cache eviction preserves pending edits and recovery copies', async ({
  page,
}, testInfo) => {
  const confirmedPage = await newPage(page, unique(testInfo.project.name, 'Confirmed for cleanup'))
  const status = page.getByTestId('sync-status')
  await page.getByTestId('editor').locator('.ProseMirror').click()
  await page.keyboard.type('confirmed before eviction')
  await expect(status).toHaveAttribute('data-unsent', 'false')
  await expect(status).toHaveAttribute('data-local', 'saved')
  const currentPage = await newPage(page, unique(testInfo.project.name, 'Cache cleanup'))
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-local', 'saved')
  const protectedPages = await page.evaluate(
    async ({ confirmedPage, currentPage }) => {
      const request = indexedDB.open('inkpact-offline')
      const database = await new Promise<IDBDatabase>((resolve, reject) => {
        request.onsuccess = () => resolve(request.result)
        request.onerror = () => reject(request.error)
      })
      const read = database.transaction('pages', 'readonly')
      const all = read.objectStore('pages').getAll()
      const stored = await new Promise<CachedPage[]>((resolve, reject) => {
        read.oncomplete = () => resolve(all.result)
        read.onabort = () => reject(read.error)
      })
      const example = stored.find((item) => item.pageId === currentPage)!
      const confirmed = stored.find((item) => item.pageId === confirmedPage)!
      const write = database.transaction(['pages', 'snapshots', 'recoveries'], 'readwrite')
      const pages = write.objectStore('pages')
      const snapshots = write.objectStore('snapshots')
      pages.put({ ...confirmed, lastOpened: 0 })
      for (let index = 0; index < 43; index++) {
        const pageId = crypto.randomUUID()
        const metadata = { ...example, pageId, ready: true, lastOpened: index + 1 }
        pages.put(metadata)
        snapshots.put({
          userId: metadata.userId,
          workspaceId: metadata.workspaceId,
          pageId,
          writerId: crypto.randomUUID(),
          update: new Uint8Array([0, 0]),
          unconfirmed: 0,
        })
      }
      const pendingPage = crypto.randomUUID()
      const recoveryPage = crypto.randomUUID()
      for (const [pageId, unconfirmed] of [
        [pendingPage, 1],
        [recoveryPage, 0],
      ] as const) {
        const metadata = { ...example, userId: 'other-user', pageId, ready: true, lastOpened: 0 }
        pages.put(metadata)
        snapshots.put({
          userId: metadata.userId,
          workspaceId: metadata.workspaceId,
          pageId,
          writerId: crypto.randomUUID(),
          update: new Uint8Array([0, 0]),
          unconfirmed,
        })
      }
      write.objectStore('recoveries').put({
        userId: 'other-user',
        workspaceId: example.workspaceId,
        pageId: recoveryPage,
        id: crypto.randomUUID(),
        before: new Uint8Array([0, 0]),
      })
      await new Promise<void>((resolve, reject) => {
        write.oncomplete = () => resolve()
        write.onabort = () => reject(write.error)
      })
      database.close()
      return { pendingPage, recoveryPage }
    },
    { confirmedPage, currentPage },
  )
  await page.reload()
  await expect(
    page.getByText(/Some older synced pages are no longer available offline/),
  ).toBeVisible()
  const remaining = await page.evaluate(
    async ({ confirmedPage, pendingPage, recoveryPage }) => {
      const request = indexedDB.open('inkpact-offline')
      const database = await new Promise<IDBDatabase>((resolve, reject) => {
        request.onsuccess = () => resolve(request.result)
        request.onerror = () => reject(request.error)
      })
      const transaction = database.transaction(['pages', 'snapshots', 'recoveries'], 'readonly')
      const pages = transaction.objectStore('pages').getAll()
      const snapshots = transaction.objectStore('snapshots').getAll()
      const recoveries = transaction.objectStore('recoveries').getAll()
      const result = await new Promise<{
        clean: number
        confirmedEvicted: boolean
        pending: boolean
        recovery: boolean
      }>((resolve, reject) => {
        transaction.oncomplete = () =>
          resolve({
            clean: pages.result.filter(
              (item) => item.ready && item.pageId !== pendingPage && item.pageId !== recoveryPage,
            ).length,
            confirmedEvicted: !pages.result.some((item) => item.pageId === confirmedPage),
            pending:
              pages.result.some((item) => item.pageId === pendingPage) &&
              snapshots.result.some(
                (item) => item.pageId === pendingPage && item.unconfirmed === 1,
              ),
            recovery:
              pages.result.some((item) => item.pageId === recoveryPage) &&
              recoveries.result.some((item) => item.pageId === recoveryPage),
          })
        transaction.onabort = () => reject(transaction.error)
      })
      database.close()
      return result
    },
    { confirmedPage, ...protectedPages },
  )
  expect(remaining).toEqual({ clean: 40, confirmedEvicted: true, pending: true, recovery: true })
})

test('upgrading an earlier offline database keeps snapshots and recovery copies', async ({
  page,
}) => {
  await page.route('**/assets/*.js', (route) => route.abort())
  await page.goto('/')
  const pageId = await page.evaluate(async () => {
    const pageId = crypto.randomUUID()
    const request = indexedDB.open('inkpact-offline', 1)
    await new Promise<void>((resolve, reject) => {
      request.onupgradeneeded = () => {
        const database = request.result
        const pages = database.createObjectStore('pages', {
          keyPath: ['userId', 'workspaceId', 'pageId'],
        })
        pages.createIndex('byUserPage', ['userId', 'pageId'], { unique: true })
        const snapshots = database.createObjectStore('snapshots', {
          keyPath: ['userId', 'workspaceId', 'pageId', 'writerId'],
        })
        snapshots.createIndex('byPage', ['userId', 'workspaceId', 'pageId'])
        const recoveries = database.createObjectStore('recoveries', { keyPath: 'id' })
        recoveries.createIndex('byUser', 'userId')
        const key = { userId: 'legacy-user', workspaceId: 'legacy-workspace', pageId }
        pages.put({
          ...key,
          title: 'Cached before upgrade',
          inTrash: false,
          ownTrash: false,
          canEdit: true,
          ready: true,
          pinned: true,
          lastOpened: 1,
        })
        snapshots.put({
          ...key,
          writerId: 'legacy-writer',
          update: new Uint8Array([0, 0]),
        })
        recoveries.put({ ...key, id: 'legacy-recovery', before: new Uint8Array([0, 0]) })
      }
      request.onsuccess = () => {
        request.result.close()
        resolve()
      }
      request.onerror = () => reject(request.error)
    })
    return pageId
  })

  await page.unroute('**/assets/*.js')
  await page.reload()
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          new Promise<number>((resolve, reject) => {
            const request = indexedDB.open('inkpact-offline')
            request.onsuccess = () => {
              const version = request.result.version
              request.result.close()
              resolve(version)
            }
            request.onerror = () => reject(request.error)
          }),
      ),
    )
    .toBe(2)
  const migrated = await page.evaluate(async (pageId) => {
    const request = indexedDB.open('inkpact-offline')
    const database = await new Promise<IDBDatabase>((resolve, reject) => {
      request.onsuccess = () => resolve(request.result)
      request.onerror = () => reject(request.error)
    })
    const transaction = database.transaction(['pages', 'snapshots', 'recoveries'], 'readonly')
    const pages = transaction.objectStore('pages').getAll()
    const snapshots = transaction.objectStore('snapshots').getAll()
    const recoveries = transaction.objectStore('recoveries').getAll()
    const result = await new Promise<{ page: boolean; pending: boolean; recovery: boolean }>(
      (resolve, reject) => {
        transaction.oncomplete = () =>
          resolve({
            page: pages.result.some((item) => item.pageId === pageId),
            pending: snapshots.result.some(
              (item) => item.pageId === pageId && item.unconfirmed === 1,
            ),
            recovery: recoveries.result.some((item) => item.pageId === pageId),
          })
        transaction.onabort = () => reject(transaction.error)
      },
    )
    database.close()
    return result
  }, pageId)
  expect(migrated).toEqual({ page: true, pending: true, recovery: true })
})

test('an edit removed elsewhere is recovered after an offline reload', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  test.setTimeout(90_000)
  const title = unique(testInfo.project.name, 'Reloaded recovery')
  const id = await newPage(page, title)
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await editor.click()
  await page.keyboard.type('first')
  await page.keyboard.press('Enter')
  await page.keyboard.type('second')
  await expect(status).toHaveAttribute('data-unsent', 'false')
  await expect(status).toHaveAttribute('data-local', 'saved')
  const other = await openClient(browser, testInfo, baseURL, id)
  await expect(other.editor).toContainText('second')

  await page.context().setOffline(true)
  await expect(status).toHaveAttribute('data-state', 'offline')
  await editor.locator('p').last().click()
  await page.keyboard.press('End')
  await page.keyboard.type(' offline-edit')
  await expect(status).toHaveAttribute('data-local', 'saved')
  await page.reload()
  await expect(editor).toContainText('second offline-edit')

  await removeLastBlock(other, 'second', /^firstsecond$/)
  await page.context().setOffline(false)
  const notices = page.getByRole('region', { name: /Notifications/ })
  await expect(notices.getByText(/It is kept in a copy beside it\./)).toBeVisible({
    timeout: 60_000,
  })
  await notices.getByRole('button', { name: 'Open the copy' }).click()
  await expect(page.getByTestId('page-title')).toHaveValue(`${title} (your offline changes)`)
  await expect(editor).toContainText('second offline-edit')
  await other.context.close()
})

test('revoked page access does not replay a pending local edit', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Access changed'))
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  const other = await openClient(browser, testInfo, baseURL, id)
  await expect(status).toHaveAttribute('data-local', 'saved')
  let forbidden = false
  await page.route(`**/api/pages/${id}`, async (route) => {
    if (forbidden && route.request().method() === 'GET') {
      await route.fulfill({
        status: 403,
        contentType: 'application/json',
        body: '{"code":"forbidden"}',
      })
    } else await route.continue()
  })
  await page.context().setOffline(true)
  await expect(status).toHaveAttribute('data-state', 'offline')
  await editor.click()
  await page.keyboard.type('permission-pending')
  await expect(status).toHaveAttribute('data-local', 'saved')
  forbidden = true
  await page.context().setOffline(false)
  await expect(page.getByText(/Your unsynced changes are still on this device/)).toBeVisible({
    timeout: 15_000,
  })
  await expect(other.editor).not.toContainText('permission-pending')

  forbidden = false
  await page.context().setOffline(true)
  await expect(page.getByText(/Offline mode:/)).toBeVisible()
  await page.context().setOffline(false)
  await expect(editor).toContainText('permission-pending')
  await expect(status).toHaveAttribute('data-unsent', 'false', { timeout: 15_000 })
  await expect(other.editor).toContainText('permission-pending', { timeout: 15_000 })
  await other.context.close()
})

test('a waiting worker never takes over a tab with pending edits', async ({ page }, testInfo) => {
  test.setTimeout(90_000)
  let cutSocket = false
  const sockets: WebSocketRoute[] = []
  await page.routeWebSocket(/\/api\/sync(?:\?.*)?$/, (route) => {
    if (cutSocket) void route.close()
    else {
      sockets.push(route)
      route.connectToServer()
    }
  })
  const id = await newPage(page, unique(testInfo.project.name, 'Worker update'))
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await expect(status).toHaveAttribute('data-local', 'saved')
  await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined))
  await page.reload()
  await expect(status).toHaveAttribute('data-state', 'live')
  await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true)
  const originalWorker = await page.evaluate(() => navigator.serviceWorker.controller!.scriptURL)
  await page.evaluate(() => caches.open('inkpact-shell-obsolete-b6-test'))
  cutSocket = true
  for (const socket of sockets) void socket.close()
  await expect(status).toHaveAttribute('data-state', 'offline')
  await editor.click()
  await page.keyboard.type('pending-update')
  await expect(status).toHaveAttribute('data-local', 'saved')

  await page.evaluate(async () => {
    await navigator.serviceWorker.register(`/service-worker.js?candidate=${Date.now()}`)
  })
  await expect
    .poll(() =>
      page.evaluate(async () => !!(await navigator.serviceWorker.getRegistration())?.waiting),
    )
    .toBe(true)
  await expect(page.getByText(/Wait until your changes reach the server/)).toBeVisible()
  expect(await page.evaluate(() => navigator.serviceWorker.controller!.scriptURL)).toBe(
    originalWorker,
  )
  expect(await page.evaluate(() => caches.has('inkpact-shell-obsolete-b6-test'))).toBe(true)

  const context = page.context()
  await page.close()
  const reopened = await context.newPage()
  await reopened.goto(`/p/${id}`)
  await expect
    .poll(() =>
      reopened.evaluate(
        async () => (await navigator.serviceWorker.getRegistration())?.active?.scriptURL,
      ),
    )
    .toContain('candidate=')
  await expect
    .poll(() => reopened.evaluate(() => caches.has('inkpact-shell-obsolete-b6-test')))
    .toBe(false)
  await expect(reopened.getByTestId('editor')).toContainText('pending-update')
  await expect(reopened.getByTestId('sync-status')).toHaveAttribute('data-unsent', 'false', {
    timeout: 15_000,
  })
  await reopened.close()
})

test('IndexedDB recovers without losing an edit held by the live editor', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  await page.addInitScript(() => {
    const state = window as Window & { __allowIndexedDB?: boolean }
    state.__allowIndexedDB = sessionStorage.getItem('allowIndexedDB') === 'true'
    const open = IDBFactory.prototype.open
    IDBFactory.prototype.open = function (this: IDBFactory, name: string, version?: number) {
      if (!state.__allowIndexedDB)
        throw new DOMException('Storage is unavailable', 'QuotaExceededError')
      return open.call(this, name, version)
    }
  })
  const id = await newPage(page, unique(testInfo.project.name, 'Storage recovery'))
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await expect(
    page.getByRole('alert').filter({ hasText: 'could not save this page locally' }),
  ).toBeVisible()
  await page.context().setOffline(true)
  await expect(status).toHaveAttribute('data-state', 'offline')
  await editor.click()
  await page.keyboard.type('not yet durable')
  await expect(status).toHaveAttribute('data-unsent', 'true')
  const warned = await page.evaluate(() => {
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    return event.defaultPrevented
  })
  expect(warned).toBe(true)

  await page.evaluate(() => {
    sessionStorage.setItem('allowIndexedDB', 'true')
    ;(window as Window & { __allowIndexedDB?: boolean }).__allowIndexedDB = true
  })
  await page.context().setOffline(false)
  await expect(status).toHaveAttribute('data-state', 'live', { timeout: 15_000 })
  await expect(status).toHaveAttribute('data-unsent', 'false', { timeout: 15_000 })
  await expect(status).toHaveAttribute('data-local', 'saved', { timeout: 15_000 })
  await expect(editor).toContainText('not yet durable')

  const other = await openClient(browser, testInfo, baseURL, id)
  await expect(other.editor).toContainText('not yet durable')
  await other.context.close()

  await page.reload()
  await expect(page.getByTestId('editor').locator('.ProseMirror')).toContainText('not yet durable')
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-local', 'saved')
})

test('an account change hides pending edits and only the original account can resume them', async ({
  page,
  browser,
  baseURL,
}, testInfo) => {
  const id = await newPage(page, unique(testInfo.project.name, 'Account boundary'))
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await expect(status).toHaveAttribute('data-local', 'saved')
  const originalUser = await page.evaluate(() => localStorage.getItem('inkpact.offline.user'))
  expect(originalUser).toBeTruthy()
  const otherTab = await page.context().newPage()
  await otherTab.goto('/')

  await page.context().setOffline(true)
  await expect(status).toHaveAttribute('data-state', 'offline')
  await editor.click()
  await page.keyboard.type('private-pending')
  await expect(status).toHaveAttribute('data-local', 'saved')
  await otherTab.evaluate(() => localStorage.setItem('inkpact.offline.user', 'different-account'))
  await expect(page.getByRole('heading', { name: 'Your session changed' })).toBeVisible()
  await expect(page.getByText('private-pending')).toHaveCount(0)

  await page.context().setOffline(false)
  const fresh = await openClient(browser, testInfo, baseURL, id)
  await expect(fresh.editor).not.toContainText('private-pending')
  await otherTab.evaluate(
    (userId) => localStorage.setItem('inkpact.offline.user', userId!),
    originalUser,
  )
  await page.getByRole('button', { name: 'Continue with the current session' }).click()
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-unsent', 'false', {
    timeout: 15_000,
  })
  await expect(fresh.editor).toHaveText('private-pending', { timeout: 15_000 })
  await otherTab.close()
  await fresh.context.close()
})

test('an expired session preserves pending edits until the same account signs in again', async ({
  browser,
  baseURL,
}, testInfo) => {
  const context = await browser.newContext({
    ...(testInfo.project.use as BrowserContextOptions),
    baseURL,
    storageState: SIGNED_OUT,
  })
  const page = await context.newPage()
  await signInThroughTheForm(page)
  await expect(page.getByRole('heading', { name: 'Welcome', exact: true })).toBeVisible()
  const id = await newPage(page, unique(testInfo.project.name, 'Session expiry'))
  const status = page.getByTestId('sync-status')
  const editor = page.getByTestId('editor').locator('.ProseMirror')
  await expect(status).toHaveAttribute('data-local', 'saved')
  await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined))
  const otherContext = await browser.newContext({
    ...(testInfo.project.use as BrowserContextOptions),
    baseURL,
    storageState: await context.storageState(),
  })
  const otherPage = await otherContext.newPage()
  await otherPage.goto('/')

  await context.setOffline(true)
  await expect(status).toHaveAttribute('data-state', 'offline')
  await editor.click()
  await page.keyboard.type('session-pending')
  await expect(status).toHaveAttribute('data-local', 'saved')
  await signOut(otherPage)
  await context.setOffline(false)
  await expect(page.getByRole('heading', { name: 'Your session changed' })).toBeVisible({
    timeout: 15_000,
  })
  await expect(page.getByText('session-pending')).toHaveCount(0)
  await page.getByRole('button', { name: 'Continue with the current session' }).click()
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible()
  await page.getByLabel('Email').fill(ADMIN.email)
  await page.getByLabel('Password').fill(ADMIN.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).toHaveURL((url) => url.pathname === `/p/${id}`)
  await expect(editor).toContainText('session-pending')
  await expect(status).toHaveAttribute('data-unsent', 'false', { timeout: 15_000 })
  await otherContext.close()
  await context.close()
})

/** The ids of the pages this browser profile has cached. */
async function cachedPageIds(page: import('@playwright/test').Page): Promise<string[]> {
  return page.evaluate(async () => {
    const request = indexedDB.open('inkpact-offline')
    const database = await new Promise<IDBDatabase>((resolve, reject) => {
      request.onsuccess = () => resolve(request.result)
      request.onerror = () => reject(request.error)
    })
    const transaction = database.transaction('pages', 'readonly')
    const pages = transaction.objectStore('pages').getAll()
    const ids = await new Promise<string[]>((resolve, reject) => {
      transaction.oncomplete = () => resolve(pages.result.map((cached) => cached.pageId as string))
      transaction.onabort = () => reject(transaction.error)
    })
    database.close()
    return ids
  })
}

test('signing out removes the pages the server has from the device, and keeps one with unsent edits', async ({
  browser,
  baseURL,
}, testInfo) => {
  // A session of its own: signing out ends it, and the other tests share the stored one.
  const context = await browser.newContext({
    ...(testInfo.project.use as BrowserContextOptions),
    baseURL,
    storageState: SIGNED_OUT,
  })
  const page = await context.newPage()
  let cutSocket = false
  const sockets: WebSocketRoute[] = []
  await page.routeWebSocket(/\/api\/sync(?:\?.*)?$/, (route) => {
    if (cutSocket) void route.close()
    else {
      sockets.push(route)
      route.connectToServer()
    }
  })
  await signInThroughTheForm(page)
  await expect(page.getByRole('heading', { name: 'Welcome', exact: true })).toBeVisible()
  const status = page.getByTestId('sync-status')
  const synced = await newPage(page, unique(testInfo.project.name, 'Signed out synced'))
  await expect(status).toHaveAttribute('data-local', 'saved')
  await expect(status).toHaveAttribute('data-unsent', 'false')

  const pending = await newPage(page, unique(testInfo.project.name, 'Signed out pending'))
  await expect(status).toHaveAttribute('data-local', 'saved')
  cutSocket = true
  for (const socket of sockets) void socket.close()
  await expect(status).toHaveAttribute('data-state', 'offline')
  await page.getByTestId('editor').locator('.ProseMirror').click()
  await page.keyboard.type('kept-for-later')
  await expect(status).toHaveAttribute('data-unsent', 'true')
  await expect(status).toHaveAttribute('data-local', 'saved')
  expect(await cachedPageIds(page)).toEqual(expect.arrayContaining([synced, pending]))

  await signOut(page)
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible()
  await expect.poll(() => cachedPageIds(page)).not.toContain(synced)
  expect(await cachedPageIds(page)).toContain(pending)
  await context.close()
})

test('Update now takes the waiting version in the only tab, and asks to close the others first', async ({
  page,
}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'The worker lifecycle is checked on desktop.')
  test.setTimeout(90_000)
  await newPage(page, unique(testInfo.project.name, 'Update now'))
  const status = page.getByTestId('sync-status')
  await expect(status).toHaveAttribute('data-local', 'saved')
  await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined))
  await page.reload()
  await expect(status).toHaveAttribute('data-state', 'live')
  await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true)
  await page.evaluate(() => caches.open('inkpact-shell-obsolete-update-now'))
  const second = await page.context().newPage()
  await second.goto('/')
  await expect(second.getByTestId('user-name').first()).toBeAttached()

  await page.evaluate(async () => {
    await navigator.serviceWorker.register(`/service-worker.js?candidate=${Date.now()}`)
  })
  await expect
    .poll(() =>
      page.evaluate(async () => !!(await navigator.serviceWorker.getRegistration())?.waiting),
    )
    .toBe(true)
  await expect(page.getByTestId('pwa-update')).toContainText('An update is ready')
  await page.getByRole('button', { name: 'Update now', exact: true }).click()
  await expect(page.getByText('Close the other tabs, then update.')).toBeVisible()
  expect(await page.evaluate(() => navigator.serviceWorker.controller!.scriptURL)).not.toContain(
    'candidate=',
  )

  await second.close()
  await page.getByRole('button', { name: 'Update now', exact: true }).click()
  // The tab reloads: a check that meets the reload is simply asked again.
  await expect
    .poll(() =>
      page.evaluate(() => navigator.serviceWorker.controller?.scriptURL ?? '').catch(() => ''),
    )
    .toContain('candidate=')
  await expect(status).toHaveAttribute('data-state', 'live')
  await expect
    .poll(() => page.evaluate(() => caches.has('inkpact-shell-obsolete-update-now')))
    .toBe(false)
  // (The notice comes back here only because the app registers /service-worker.js again, a different address from the
  // candidate this test made: in a real update the two are the same.)
})
