import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { defineComponent } from 'vue'
import { formatWait, useErrorMessage } from './errors'
import { createAppI18n, type Locale } from './i18n'

function messageFor(code: string, retryAfterSeconds: number | undefined, locale: Locale = 'en') {
  let result = ''
  const Probe = defineComponent({
    setup() {
      result = useErrorMessage()(code, retryAfterSeconds)
      return () => null
    },
  })
  mount(Probe, { global: { plugins: [createAppI18n(locale)] } })
  return result
}

describe('formatWait', () => {
  it.each([
    [1, 'en', 'in 1 second'],
    [45, 'en', 'in 45 seconds'],
    [59.2, 'en', 'in 60 seconds'],
    [60, 'en', 'in 1 minute'],
    [61, 'en', 'in 2 minutes'],
    [900, 'en', 'in 15 minutes'],
    [45, 'nl', 'over 45 seconden'],
    [900, 'nl', 'over 15 minuten'],
  ])('%s seconds in %s is "%s"', (seconds, locale, expected) => {
    expect(formatWait(seconds, locale)).toBe(expected)
  })

  it('never says zero', () => {
    expect(formatWait(0, 'en')).toBe('in 1 second')
  })
})

describe('useErrorMessage', () => {
  it('says how long to wait for a lockout, in the language of the page', () => {
    expect(messageFor('login_locked', 120)).toBe(
      'Too many wrong passwords for this account. Try again in 2 minutes.',
    )
    expect(messageFor('login_locked', 120, 'nl')).toBe(
      'Te veel verkeerde wachtwoorden voor dit account. Probeer het opnieuw over 2 minuten.',
    )
  })

  it('says how long to wait for a rate limit', () => {
    expect(messageFor('too_many_requests', 30)).toBe(
      'Too many attempts from this device. Try again in 30 seconds.',
    )
  })

  it('assumes a minute when the server did not say', () => {
    expect(messageFor('login_locked', undefined)).toContain('in 1 minute')
  })

  it.each([
    'invalid_credentials',
    'setup_code_invalid',
    'setup_not_available',
    'email_invalid',
    'display_name_invalid',
    'workspace_name_invalid',
    'password_too_short',
    'password_too_long',
    'password_same_as_email',
    'not_authenticated',
    'forbidden',
    'not_found',
    'csrf_invalid',
    'network_error',
    'server_error',
  ])('has a message for %s in both languages, and not the generic one', (code) => {
    for (const locale of ['en', 'nl'] as const) {
      const message = messageFor(code, undefined, locale)
      expect(message).not.toBe('')
      expect(message).not.toBe(messageFor('some_future_code', undefined, locale))
      expect(message).not.toContain('{')
    }
  })

  it('has a generic message for a code it does not know', () => {
    expect(messageFor('some_future_code', undefined)).toBe('Something went wrong. Try again.')
  })
})
