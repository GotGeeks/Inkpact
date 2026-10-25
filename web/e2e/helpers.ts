import type { Page } from '@playwright/test'
import { expect } from './fixtures'
import { ADMIN } from './credentials'

/** Signs in through the login page, the way a person does, and waits for the home page. */
export async function signInThroughTheForm(
  page: Page,
  password: string = ADMIN.password,
  email: string = ADMIN.email,
) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

export async function expectHome(page: Page) {
  await expect(
    page.getByRole('heading', { level: 1, name: `Signed in as ${ADMIN.displayName}` }),
  ).toBeVisible()
}

export async function expectLoginPage(page: Page) {
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
}
