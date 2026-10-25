import { SIGNED_OUT } from './credentials'
import { expect, test } from './fixtures'
import { expectHome, expectLoginPage, signInThroughTheForm } from './helpers'

// Locks an address that has no account, one per project, so the shared administrator is never touched.
// The lockout treats an address with no account exactly like one with an account.
test.use({ storageState: SIGNED_OUT })

test('five wrong passwords lock an address, and the page says for how long', async ({
  page,
}, testInfo) => {
  const email = `locked-${testInfo.project.name}@example.com`

  for (let attempt = 1; attempt <= 5; attempt++) {
    await signInThroughTheForm(page, `wrong password ${attempt}`, email)
    await expect(page.getByRole('alert')).toHaveText('The email or password is not right.')
  }

  await signInThroughTheForm(page, 'wrong password 6', email)

  await expect(page.getByRole('alert')).toHaveText(
    'Too many wrong passwords for this account. Try again in 1 minute.',
  )
  await expectLoginPage(page)
})

test('the lockout message is in Dutch on a Dutch page', async ({ page }, testInfo) => {
  const email = `locked-nl-${testInfo.project.name}@example.com`
  await page.goto('/login')
  await page.getByRole('button', { name: 'Nederlands' }).click()

  for (let attempt = 1; attempt <= 6; attempt++) {
    await page.getByLabel('E-mailadres').fill(email)
    await page.getByLabel('Wachtwoord').fill(`wrong password ${attempt}`)
    await page.getByRole('button', { name: 'Inloggen' }).click()
    await expect(page.getByRole('alert')).toBeVisible()
  }

  await expect(page.getByRole('alert')).toHaveText(
    'Te veel verkeerde wachtwoorden voor dit account. Probeer het opnieuw over 1 minuut.',
  )
})

test('a locked address does not stop anyone else from signing in', async ({ page }, testInfo) => {
  const email = `locked-other-${testInfo.project.name}@example.com`
  for (let attempt = 1; attempt <= 6; attempt++) {
    await signInThroughTheForm(page, `wrong password ${attempt}`, email)
    await expect(page.getByRole('alert')).toBeVisible()
  }

  await signInThroughTheForm(page)

  await expectHome(page)
})
