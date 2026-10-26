import { ADMIN, SIGNED_OUT } from './credentials'
import { expect, test } from './fixtures'
import {
  chooseLanguage,
  expectHome,
  expectLoginPage,
  signInThroughTheForm,
  signOut,
} from './helpers'

test.describe('signed in', () => {
  test('the start page shows after signing in, and survives a reload', async ({ page }) => {
    await page.goto('/')
    await expectHome(page)

    await page.reload()
    await expectHome(page)
  })

  test('the login and setup pages lead back home', async ({ page }) => {
    await page.goto('/login')
    await expectHome(page)

    await page.goto('/setup')
    await expectHome(page)
  })

  test('the language choice is kept for the next visit', async ({ page }) => {
    await page.goto('/')
    await chooseLanguage(page, 'Nederlands')
    await expectHome(page, 'Welkom')
    await expect(page.locator('html')).toHaveAttribute('lang', 'nl')

    await page.reload()
    await expectHome(page, 'Welkom')

    await chooseLanguage(page, 'English')
    await expectHome(page)
  })

  test('the page does not scroll sideways', async ({ page }) => {
    await page.goto('/')
    await expectHome(page)

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    )
    expect(overflow).toBeLessThanOrEqual(0)
  })
})

test.describe('signed out', () => {
  test.use({ storageState: SIGNED_OUT })

  test('a visitor lands on the login page, with the right fields', async ({ page }) => {
    await page.goto('/')

    await expect(page).toHaveURL(/\/login$/)
    await expectLoginPage(page)
    await expect(page.getByLabel('Email')).toHaveAttribute('autocomplete', 'email')
    await expect(page.getByLabel('Password')).toHaveAttribute('autocomplete', 'current-password')
  })

  test('the setup page is closed once the account exists', async ({ page }) => {
    await page.goto('/setup')

    await expect(page).toHaveURL(/\/login$/)
  })

  // Wrong passwords are tried with an address nobody has, one per project: the administrator is shared by all
  // the projects at once and must never end up locked. An unknown address is treated exactly like a known one.
  test('a wrong password shows an error and keeps the email', async ({ page }, testInfo) => {
    const email = `nobody-${testInfo.project.name}-1@example.com`
    await signInThroughTheForm(page, 'not the password at all', email)

    await expect(page.getByRole('alert')).toHaveText('The email or password is not right.')
    await expect(page.getByLabel('Email')).toHaveValue(email)
    await expect(page.getByLabel('Password')).toHaveValue('')
    await expect(page).toHaveURL(/\/login$/)
  })

  test('the error is in Dutch when the page is in Dutch', async ({ page }, testInfo) => {
    await page.goto('/login')
    await page.getByRole('button', { name: 'Nederlands' }).click()
    await page.getByLabel('E-mailadres').fill(`nobody-${testInfo.project.name}-2@example.com`)
    await page.getByLabel('Wachtwoord').fill('not the password at all')
    await page.getByRole('button', { name: 'Inloggen' }).click()

    await expect(page.getByRole('alert')).toHaveText('Het e-mailadres of wachtwoord klopt niet.')
  })

  test('signing in works with the keyboard alone', async ({ page, isMobile }) => {
    test.skip(isMobile, 'Tab order is for keyboards')
    await page.goto('/login')

    await page.getByLabel('Email').focus()
    await page.keyboard.type(ADMIN.email)
    await page.keyboard.press('Tab')
    await page.keyboard.type(ADMIN.password)
    await page.keyboard.press('Enter')

    await expectHome(page)
  })

  test('signing in goes back to the page that was asked for', async ({ page }) => {
    await page.goto('/?from=a-link')
    await expect(page).toHaveURL(/\/login\?redirect=/)
    expect(new URL(page.url()).searchParams.get('redirect')).toBe('/?from=a-link')

    await page.getByLabel('Email').fill(ADMIN.email)
    await page.getByLabel('Password').fill(ADMIN.password)
    await page.getByRole('button', { name: 'Sign in' }).click()

    await expectHome(page)
    await expect(page).toHaveURL(/\/\?from=a-link$/)
  })

  test('a redirect to another site is ignored', async ({ page }) => {
    await page.goto('/login?redirect=//evil.example/steal')
    await page.getByLabel('Email').fill(ADMIN.email)
    await page.getByLabel('Password').fill(ADMIN.password)
    await page.getByRole('button', { name: 'Sign in' }).click()

    await expectHome(page)
    expect(new URL(page.url()).hostname).not.toBe('evil.example')
  })

  test('the session cookie is HttpOnly, SameSite=Lax and unreadable from the page', async ({
    page,
    context,
    browserName,
  }) => {
    await signInThroughTheForm(page)
    await expectHome(page)

    const cookies = await context.cookies()
    const session = cookies.filter((cookie) => /inkpact$/.test(cookie.name))
    expect(session).toHaveLength(1)
    expect(session[0]).toMatchObject({ httpOnly: true, path: '/' })
    // WebKit reports Lax as None here; the server tests check the header it sends.
    if (browserName !== 'webkit') expect(session[0]!.sameSite).toBe('Lax')
    expect(await page.evaluate(() => document.cookie)).toBe('')
  })

  test('signing out ends this session, for good', async ({ page }) => {
    await signInThroughTheForm(page)
    await expectHome(page)

    await signOut(page)
    await expectLoginPage(page)

    await page.reload()
    await expectLoginPage(page)
    await page.goto('/')
    await expectLoginPage(page)
  })

  test('signing out in one browser leaves the other signed in', async ({ page, browser }) => {
    await signInThroughTheForm(page)
    await expectHome(page)
    const other = await browser.newContext()
    const otherPage = await other.newPage()
    await signInThroughTheForm(otherPage)
    await expectHome(otherPage)

    await signOut(page)
    await expectLoginPage(page)

    await otherPage.reload()
    await expectHome(otherPage)
    await other.close()
  })
})
