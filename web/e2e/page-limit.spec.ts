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
