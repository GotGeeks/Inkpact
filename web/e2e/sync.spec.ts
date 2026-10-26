import { expect, test } from './fixtures'
import { createPageNamed, pageIdOf } from './helpers'
import { openSyncSocket, sendFrame, socketLog } from './syncSocket'

const SUBSCRIBE = 0
const UPDATE = 2
const SYNC = 3
const PING = 5

// The real client and the browser scenarios (typing together, going offline) come with the editor in run 1.11. This
// checks what only a real browser can: that the strict policy lets the app open its own socket (connect-src 'self'
// must cover ws: and wss:, which older WebKit did not), that the session cookie goes along, and that two tabs see each
// other's updates.
test('the app opens its own sync socket under the strict policy, and two tabs see each other', async ({
  page,
  context,
}, testInfo) => {
  await page.goto('/')
  const pageId = await pageIdOf(
    await createPageNamed(page, `Sync-${testInfo.project.name}-${Date.now().toString(36)}`),
  )
  const other = await context.newPage()
  await other.goto('/')

  const opened = await openSyncSocket(page, 'a')
  expect(opened).toMatchObject({ open: true, closed: null })
  await openSyncSocket(other, 'b')

  await sendFrame(page, 'a', SUBSCRIBE, pageId)
  await sendFrame(other, 'b', SUBSCRIBE, pageId)
  for (const [tab, name] of [
    [page, 'a'],
    [other, 'b'],
  ] as const) {
    await expect
      .poll(async () => (await socketLog(tab, name)).frames.map((frame) => frame.type))
      .toEqual([SYNC])
  }

  await sendFrame(page, 'a', UPDATE, pageId, [1, 2, 3])

  await expect
    .poll(async () => (await socketLog(other, 'b')).frames.at(-1))
    .toEqual({ type: UPDATE, pageId, bytes: [1, 2, 3] })
  // The sender does not get its own update back.
  await sendFrame(page, 'a', PING)
  await expect.poll(async () => (await socketLog(page, 'a')).pongs).toBe(1)
  expect((await socketLog(page, 'a')).frames.map((frame) => frame.type)).toEqual([SYNC])
  await other.close()
})

test('a page that is not there is refused on the socket, not by closing it', async ({ page }) => {
  await page.goto('/')
  await openSyncSocket(page, 'a')

  await sendFrame(page, 'a', SUBSCRIBE, '00000000-0000-0000-0000-000000000000')

  await expect
    .poll(async () => (await socketLog(page, 'a')).frames.map((frame) => frame.type))
    .toEqual([7])
  expect((await socketLog(page, 'a')).closed).toBeNull()
})
