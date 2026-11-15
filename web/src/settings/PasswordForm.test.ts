import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { jsonResponse } from '../testing/helpers'
import PasswordForm from './PasswordForm.vue'

let wrapper: VueWrapper | null = null
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

/** The server's one answer to the change; what was sent is kept. */
function open(answer: () => Response, locale: Locale = 'en') {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    expect(`${init?.method} ${String(input)}`).toBe('POST /api/account/password')
    return answer()
  })
  vi.stubGlobal('fetch', fetchMock)
  const pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().user = { id: 'u1', email: 'jos@example.com', displayName: 'Jos', isAdmin: true }
  wrapper = mount(PasswordForm, {
    global: { plugins: [pinia, createAppI18n(locale)] },
    attachTo: document.body,
  })
  return fetchMock
}

const field = (id: string) => document.getElementById(id) as HTMLInputElement
const text = (element: Element | null | undefined) =>
  element?.textContent?.replace(/\s+/g, ' ').trim() ?? ''
const errorOf = (id: string) => text(document.getElementById(`${id}-error`))

async function fillAndSubmit(currentPassword: string, newPassword: string) {
  field('current-password').value = currentPassword
  field('current-password').dispatchEvent(new Event('input'))
  field('new-password').value = newPassword
  field('new-password').dispatchEvent(new Event('input'))
  document.body.querySelector('form')!.dispatchEvent(new Event('submit'))
  await flushPromises()
}

describe('PasswordForm', () => {
  it('sends both passwords, clears the fields, says so and tells the section', async () => {
    const fetchMock = open(() => jsonResponse(204))

    await fillAndSubmit('the old passphrase', 'a whole new passphrase')

    expect(JSON.parse(String(fetchMock.mock.calls[0]![1]!.body))).toEqual({
      currentPassword: 'the old passphrase',
      newPassword: 'a whole new passphrase',
    })
    expect(field('current-password').value).toBe('')
    expect(field('new-password').value).toBe('')
    expect(text(document.body.querySelector('[role="status"]'))).toBe(
      'Password changed. Your other devices are signed out.',
    )
    expect(wrapper!.emitted('changed')).toHaveLength(1)
  })

  it('asks the browser for the right passwords, with the setup hint under the new one', () => {
    open(() => jsonResponse(204))

    expect(field('current-password').type).toBe('password')
    expect(field('current-password').autocomplete).toBe('current-password')
    expect(field('new-password').type).toBe('password')
    expect(field('new-password').autocomplete).toBe('new-password')
    expect(text(document.getElementById('new-password-hint'))).toBe(
      'At least 12 characters. A few random words work well.',
    )
    // The account it belongs to, hidden, so a password manager updates the saved entry of that account.
    const username = document.body.querySelector<HTMLInputElement>('[autocomplete="username"]')!
    expect(username.value).toBe('jos@example.com')
    expect(username.getAttribute('aria-hidden')).toBe('true')
    expect(username.tabIndex).toBe(-1)
  })

  it('says a wrong current password under its field, and puts the focus there', async () => {
    open(() => jsonResponse(400, { code: 'current_password_invalid', field: 'currentPassword' }))

    await fillAndSubmit('a wrong guess', 'a whole new passphrase')

    expect(errorOf('current-password')).toBe('That is not your current password.')
    expect(field('current-password').getAttribute('aria-invalid')).toBe('true')
    expect(document.activeElement).toBe(field('current-password'))
    expect(document.body.querySelector('[role="status"]')).toBeNull()
    expect(wrapper!.emitted('changed')).toBeUndefined()
  })

  it.each([
    ['password_too_short', 'The password needs at least 12 characters.'],
    ['password_too_long', 'The password can be at most 128 characters.'],
    ['password_same_as_email', 'The password cannot be your email address.'],
    ['password_unchanged', 'The new password is the same as the current one.'],
  ])('says %s under the new password', async (code, said) => {
    open(() => jsonResponse(400, { code, field: 'newPassword' }))

    await fillAndSubmit('the old passphrase', 'whatever')

    expect(errorOf('new-password')).toBe(said)
    expect(errorOf('current-password')).toBe('')
    expect(document.activeElement).toBe(field('new-password'))
  })

  it('says a lock above the form, with the wait the server gave', async () => {
    open(
      () =>
        new Response(JSON.stringify({ code: 'login_locked' }), {
          status: 429,
          headers: { 'Content-Type': 'application/json', 'Retry-After': '120' },
        }),
    )

    await fillAndSubmit('the old passphrase', 'a whole new passphrase')

    expect(text(document.body.querySelector('[role="alert"]'))).toBe(
      'Too many wrong passwords for this account. Try again in 2 minutes.',
    )
    expect(errorOf('current-password')).toBe('')
  })

  it('clears an earlier error when it is tried again', async () => {
    let answer = jsonResponse(400, { code: 'current_password_invalid', field: 'currentPassword' })
    open(() => answer)
    await fillAndSubmit('a wrong guess', 'a whole new passphrase')

    answer = jsonResponse(204)
    await fillAndSubmit('the old passphrase', 'a whole new passphrase')

    expect(errorOf('current-password')).toBe('')
    expect(field('current-password').getAttribute('aria-invalid')).toBeNull()
  })

  it('speaks Dutch', async () => {
    open(
      () => jsonResponse(400, { code: 'current_password_invalid', field: 'currentPassword' }),
      'nl',
    )

    expect(text(document.querySelector('h3'))).toBe('Wachtwoord')
    await fillAndSubmit('a wrong guess', 'a whole new passphrase')

    expect(errorOf('current-password')).toBe('Dat is niet je huidige wachtwoord.')
  })
})
