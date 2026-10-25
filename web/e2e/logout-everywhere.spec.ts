import { SIGNED_OUT } from './credentials'
import { test } from './fixtures'
import { expectHome, expectLoginPage, signInThroughTheForm } from './helpers'

// Ending every session also ends the one the other projects share, so this runs last, in its own project.
test.use({ storageState: SIGNED_OUT })

test('signing out everywhere ends the session in every browser', async ({ page, browser }) => {
  await signInThroughTheForm(page)
  await expectHome(page)
  const phone = await browser.newContext()
  const phonePage = await phone.newPage()
  await signInThroughTheForm(phonePage)
  await expectHome(phonePage)

  await page.getByRole('button', { name: 'Sign out everywhere' }).click()
  await expectLoginPage(page)

  // The other browser has not done anything yet; its next request finds the session gone.
  await phonePage.reload()
  await expectLoginPage(phonePage)
  await phone.close()
})
