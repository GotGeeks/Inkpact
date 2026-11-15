import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'
import { closeDrawer, createPageNamed, pageIdOf } from './helpers'
import { refusingSocket } from './refusingSocket'

/**
 * A change the server refuses because the page reached its size limit (run B.10), through a socket route that refuses
 * as the server does (`refusingSocket`). What the server itself does is in its own tests (SyncPageLimitTests).
 */

const editorOf = (page: Page) => page.getByTestId('editor').locator('.ProseMirror')
const pill = (page: Page) => page.getByTestId('sync-status')

test('a change refused for the page size can be copied, then discarded to go on editing', async ({
  page,
}, testInfo) => {
  const socket = await refusingSocket(page)
  await page.goto('/')
  const pageId = await pageIdOf(
    await createPageNamed(page, `Full-${testInfo.project.name}-${Date.now().toString(36)}`),
  )
  socket.pageId = pageId
  await closeDrawer(page)
  const editor = editorOf(page)
  await expect(pill(page)).toHaveAttribute('data-state', 'live')
  await editor.click()
  await page.keyboard.type('kept')
  await expect(pill(page)).toHaveAttribute('data-local', 'saved')

  socket.armed = true
  // One input, so one update: the editor stops taking keys as soon as the refusal arrives.
  await page.keyboard.insertText(' refused')

  const notice = page.getByTestId('refused-full')
  await expect(notice.getByRole('alert')).toContainText('reached its size limit')
  await expect(editor).toHaveAttribute('contenteditable', 'false')
  // Still on the screen, to copy from.
  await expect(editor).toContainText('kept refused')

  await notice.getByRole('button', { name: 'Discard unsaved change' }).click()
  await expect(notice.getByText('Discard the change that was not saved?')).toBeFocused()
  await notice.getByRole('button', { name: 'Discard', exact: true }).click()

  await expect(notice).toBeHidden()
  await expect(editor).toHaveAttribute('contenteditable', 'true')
  await expect(editor).toHaveText('kept')
  // The focus goes to the new editor, not to the page.
  await expect(editor).toBeFocused()
  await expect(pill(page)).toHaveAttribute('data-state', 'live')

  // Editing goes on, and reaches the server: a reload shows it.
  await editor.click()
  await page.keyboard.press('End')
  await page.keyboard.type('!')
  await expect(pill(page)).toHaveAttribute('data-local', 'saved')
  await page.reload()
  await expect(editorOf(page)).toHaveText('kept!')
  expect(socket.refusals).toBe(1)
})

test('a refused change stays after a reload until it is discarded', async ({ page }, testInfo) => {
  const socket = await refusingSocket(page)
  await page.goto('/')
  const pageId = await pageIdOf(
    await createPageNamed(page, `Stays-${testInfo.project.name}-${Date.now().toString(36)}`),
  )
  socket.pageId = pageId
  await closeDrawer(page)
  await expect(pill(page)).toHaveAttribute('data-state', 'live')
  await editorOf(page).click()
  socket.armed = true
  await page.keyboard.insertText('refused')
  await expect(page.getByTestId('refused-full')).toBeVisible()
  // The device keeps the change; give it the moment to save it.
  await expect(pill(page)).toHaveAttribute('data-local', 'saved')

  // After the reload the route refuses it again, as the server would: the page is still full.
  socket.refused = false
  socket.armed = true
  await page.reload()

  await expect(page.getByTestId('refused-full')).toBeVisible()
  await expect(editorOf(page)).toContainText('refused')
})

test('a refused change cannot be discarded offline', async ({ page, context }, testInfo) => {
  const socket = await refusingSocket(page)
  await page.goto('/')
  socket.pageId = await pageIdOf(
    await createPageNamed(page, `Offline-${testInfo.project.name}-${Date.now().toString(36)}`),
  )
  await closeDrawer(page)
  await expect(pill(page)).toHaveAttribute('data-state', 'live')
  await editorOf(page).click()
  socket.armed = true
  await page.keyboard.insertText('refused')
  const notice = page.getByTestId('refused-full')
  await expect(notice).toBeVisible()

  await context.setOffline(true)
  const discard = notice.getByRole('button', { name: 'Discard unsaved change' })
  await expect(discard).toBeDisabled()
  await expect(notice).toContainText('Discarding needs the connection')

  await context.setOffline(false)
  await expect(discard).toBeEnabled()
})

/** A second tab of the same browser on the page, with its own socket route. */
async function secondTab(page: Page, pageId: string) {
  const other = await page.context().newPage()
  const socket = await refusingSocket(other)
  socket.pageId = pageId
  await other.goto(`/p/${pageId}`)
  await closeDrawer(other)
  await expect(pill(other)).toHaveAttribute('data-state', 'live')
  return { other, socket }
}

test('a discard in one tab discards the same refused change in another, and the device keeps none of it', async ({
  page,
}, testInfo) => {
  const socket = await refusingSocket(page)
  await page.goto('/')
  const pageId = await pageIdOf(
    await createPageNamed(page, `Tabs-${testInfo.project.name}-${Date.now().toString(36)}`),
  )
  socket.pageId = pageId
  await closeDrawer(page)
  await expect(pill(page)).toHaveAttribute('data-state', 'live')
  await editorOf(page).click()
  await page.keyboard.type('kept')
  await expect(pill(page)).toHaveAttribute('data-local', 'saved')
  const { other, socket: otherSocket } = await secondTab(page, pageId)
  await expect(editorOf(other)).toHaveText('kept')

  // Both tabs are refused: the page is full for each of them.
  socket.armed = true
  await editorOf(page).click()
  await page.keyboard.press('End')
  await page.keyboard.insertText(' refused here')
  otherSocket.armed = true
  await editorOf(other).click()
  await other.keyboard.press('End')
  await other.keyboard.insertText(' refused there')
  await expect(page.getByTestId('refused-full')).toBeVisible()
  await expect(other.getByTestId('refused-full')).toBeVisible()

  const notice = page.getByTestId('refused-full')
  await notice.getByRole('button', { name: 'Discard unsaved change' }).click()
  await notice.getByRole('button', { name: 'Discard', exact: true }).click()

  await expect(editorOf(page)).toHaveText('kept')
  await expect(other.getByTestId('refused-full')).toBeHidden()
  await expect(editorOf(other)).toHaveText('kept')
  // Nothing of either change is left on the device: reloaded, the other tab sends nothing (its route no longer refuses,
  // so whatever it had would reach the server and show).
  await other.reload()
  await expect(pill(other)).toHaveAttribute('data-state', 'live')
  await expect(editorOf(other)).toHaveText('kept')
  await other.close()
})

test('a discard in one tab leaves another tab its own unsaved edits', async ({
  page,
}, testInfo) => {
  const socket = await refusingSocket(page)
  await page.goto('/')
  const pageId = await pageIdOf(
    await createPageNamed(page, `Keep-${testInfo.project.name}-${Date.now().toString(36)}`),
  )
  socket.pageId = pageId
  await closeDrawer(page)
  await expect(pill(page)).toHaveAttribute('data-state', 'live')
  await editorOf(page).click()
  await page.keyboard.type('kept')
  await expect(pill(page)).toHaveAttribute('data-local', 'saved')
  const { other, socket: otherSocket } = await secondTab(page, pageId)
  await expect(editorOf(other)).toHaveText('kept')

  // The other tab loses its connection and types: an edit only this device has.
  otherSocket.cut(true)
  await expect(pill(other)).toHaveAttribute('data-state', 'offline')
  await editorOf(other).click()
  await other.keyboard.press('End')
  await other.keyboard.type(' theirs')
  await expect(pill(other)).toHaveAttribute('data-local', 'saved')

  socket.armed = true
  await editorOf(page).click()
  await page.keyboard.press('End')
  await page.keyboard.insertText(' refused')
  const notice = page.getByTestId('refused-full')
  await notice.getByRole('button', { name: 'Discard unsaved change' }).click()
  await notice.getByRole('button', { name: 'Discard', exact: true }).click()
  // Opened again from this device's copy, which may already hold the other tab's edit again: only the refused text goes.
  await expect(notice).toBeHidden()
  await expect(editorOf(page)).not.toContainText('refused')

  // The other tab saved its copy again; closed, its edit is still on the device, and a new tab sends it.
  await expect(pill(other)).toHaveAttribute('data-local', 'saved')
  await other.close()
  const third = await page.context().newPage()
  await third.goto(`/p/${pageId}`)
  await closeDrawer(third)
  await expect(editorOf(third)).toHaveText('kept theirs')
  await expect(editorOf(page)).toHaveText('kept theirs')
  await third.close()
})
