import { mkdirSync } from 'node:fs'
import { dirname } from 'node:path'
import { ADMIN, AUTH_STATE_FILE } from './credentials'
import { expect, test } from './fixtures'
import { expectHome } from './helpers'

/**
 * The first run of a fresh installation, which is also how every other project gets its signed-in account.
 * It runs once, before the others, against an empty database.
 */
test('the first visit asks for the administrator account, and creating it signs in', async ({
  page,
}) => {
  await page.goto('/')

  // A fresh install sends everything to the setup page, in the browser's language.
  await expect(page).toHaveURL(/\/setup$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome to Inkpact' })).toBeVisible()

  // The language can be switched before there is an account, and the choice is kept.
  await page.getByRole('button', { name: 'Nederlands' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Welkom bij Inkpact' })).toBeVisible()
  await page.getByRole('button', { name: 'English' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Welcome to Inkpact' })).toBeVisible()

  const code = page.getByLabel('Setup code')
  const name = page.getByLabel('Your name')
  const email = page.getByLabel('Email')
  const password = page.getByLabel('Password')
  const workspace = page.getByLabel('Name of your first workspace')
  const submit = page.getByRole('button', { name: 'Create account' })

  await name.fill(ADMIN.displayName)
  await email.fill(ADMIN.email)
  await workspace.fill(ADMIN.workspaceName)

  // A wrong setup code is refused and shown under its own field.
  await code.fill('NOTTHECODE')
  await password.fill(ADMIN.password)
  await submit.click()
  await expect(page.getByText('That setup code is not right')).toBeVisible()
  await expect(code).toHaveAttribute('aria-invalid', 'true')
  await expect(password).toHaveValue('')

  // A short password is refused, under its field, and nothing was created.
  await code.fill(ADMIN.setupCode)
  await password.fill('too short')
  await submit.click()
  await expect(page.getByText('The password needs at least 12 characters.')).toBeVisible()
  await expect(page).toHaveURL(/\/setup$/)

  // The right details create the account and sign in.
  await password.fill(ADMIN.password)
  await submit.click()
  await expectHome(page)

  mkdirSync(dirname(AUTH_STATE_FILE), { recursive: true })
  await page.context().storageState({ path: AUTH_STATE_FILE })
})
