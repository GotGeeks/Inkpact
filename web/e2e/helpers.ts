import type { Locator, Page } from '@playwright/test'
import { ADMIN } from './credentials'
import { expect } from './fixtures'

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

/** The start page, shown right after signing in, in English or Dutch. */
export async function expectHome(page: Page, heading = 'Welcome') {
  // Exact: the setup page's heading, "Welcome to Inkpact", must not count as the start page.
  await expect(page.getByRole('heading', { level: 1, name: heading, exact: true })).toBeVisible()
}

export async function expectLoginPage(page: Page) {
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
}

/** On a phone the pages and the menu are in a drawer behind a button; on a wide screen they are always there. */
export async function openSidebar(page: Page) {
  if (await page.getByRole('dialog').isVisible()) return
  const button = page.getByRole('button', { name: /^Open (the page list|de paginalijst)$/ })
  // The app may still be starting: wait until either the button (narrow) or the account menu (wide) is there.
  await expect(button.or(page.getByTestId('user-name'))).toBeVisible()
  if (await button.isVisible()) await button.click()
  // The tree only exists once there is a page; the account menu is always there.
  await expect(page.getByTestId('user-name')).toBeVisible()
}

async function openUserMenu(page: Page) {
  await openSidebar(page)
  // By its test id and not its name, which is different in Dutch.
  await page.getByTestId('user-name').click()
}

export async function signOut(page: Page, everywhere = false) {
  await openUserMenu(page)
  await page
    .getByRole('menuitem', { name: everywhere ? 'Sign out everywhere' : 'Sign out', exact: true })
    .click()
}

export async function chooseLanguage(page: Page, language: 'English' | 'Nederlands') {
  await openUserMenu(page)
  await page.getByRole('menuitemradio', { name: language }).click()
  // On a phone the drawer stays open behind the menu; close it, as a person would, to see the page again.
  if (await page.getByRole('dialog').isVisible()) {
    await page.keyboard.press('Escape')
    await expect(page.getByRole('dialog')).toBeHidden()
  }
}

/** On a phone the drawer stays open after a page is made in it; close it, as a person would, to see the page. */
export async function closeDrawer(page: Page) {
  if (await page.getByRole('dialog').isVisible()) {
    await page.keyboard.press('Escape')
    await expect(page.getByRole('dialog')).toBeHidden()
  }
}

/** The line of the sidebar for a page, found by its title. Titles in the tests are unique. */
export function rowFor(page: Page, title: string): Locator {
  return page.locator('[role="treeitem"]').filter({ hasText: title }).first()
}

/** Makes a page through the New page button, names it, and returns the line for it. The sidebar stays open. */
export async function createPageNamed(page: Page, title: string): Promise<Locator> {
  await openSidebar(page)
  await page.getByRole('button', { name: 'New page' }).first().click()
  const field = page.getByRole('textbox', { name: 'Page title' })
  await expect(field).toBeFocused()
  await field.fill(title)
  await field.press('Enter')
  const row = rowFor(page, title)
  await expect(row).toBeVisible()
  return row
}

/** The id of a page in the sidebar, for talking to it later without relying on its title. */
export async function pageIdOf(row: Locator): Promise<string> {
  return (await row.getAttribute('data-page-id'))!
}
