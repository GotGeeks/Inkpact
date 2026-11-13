import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { useAuthStore } from '../stores/auth'
import type { VersionInfo } from '../stores/version'
import { jsonResponse } from '../testing/helpers'
import AboutSection from './AboutSection.vue'

const notes = 'https://github.com/GotGeeks/Inkpact/releases/tag/v0.2.0'

let wrapper: VueWrapper | null = null
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

async function open(answer: () => Response, locale: Locale = 'en') {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input) !== '/api/version') throw new Error(`Unexpected request: ${String(input)}`)
    return answer()
  })
  vi.stubGlobal('fetch', fetchMock)
  const pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().user = { id: 'u1', email: 'jos@example.com', displayName: 'Jos', isAdmin: true }
  wrapper = mount(AboutSection, {
    global: { plugins: [pinia, createAppI18n(locale)] },
    attachTo: document.body,
  })
  await flushPromises()
  return fetchMock
}

const answer = (info: VersionInfo) => () => jsonResponse(200, info)
const text = (selector: string) =>
  document.body.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? ''

describe('AboutSection', () => {
  it('says which version runs', async () => {
    await open(answer({ version: '0.1.0+edge.abc1234', update: null }))

    expect(text('h2')).toBe('About Inkpact')
    expect(text('[data-testid="app-version"]')).toBe('Version 0.1.0+edge.abc1234')
    expect(document.body.querySelector('[data-testid="update"]')).toBeNull()
  })

  it('names a development build as such', async () => {
    await open(answer({ version: '0.0.0-dev', update: null }))

    expect(text('[data-testid="app-version"]')).toBe('A development build, without a version.')
  })

  it('tells of a newer release, links to its notes in a new tab, and says how to update', async () => {
    await open(answer({ version: '0.1.0', update: { version: '0.2.0', url: notes } }))

    const update = document.body.querySelector<HTMLElement>('[data-testid="update"]')!
    expect(text('[data-testid="update"] h3')).toBe('Inkpact 0.2.0 is out')
    const link = update.querySelector('a')!
    expect(link.getAttribute('href')).toBe(notes)
    expect(link.getAttribute('target')).toBe('_blank')
    expect(link.getAttribute('rel')).toBe('noopener noreferrer')
    expect(link.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'What is new in 0.2.0 (opens in a new tab)',
    )
    expect(update.textContent).toContain('Check for updates')
    expect(update.querySelector('code')?.textContent).toBe(
      'docker compose pull && docker compose up -d',
    )
  })

  it('says when the version could not be read, and reads it when asked again', async () => {
    let fail = true
    const fetchMock = await open(() =>
      fail
        ? jsonResponse(500, { code: 'server_error' })
        : jsonResponse(200, { version: '0.1.0', update: null }),
    )
    expect(text('[role="alert"]')).toContain('The version could not be loaded.')

    fail = false
    ;[...document.body.querySelectorAll('button')]
      .find((b) => b.textContent?.includes('Try again'))!
      .click()
    await flushPromises()

    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(text('[data-testid="app-version"]')).toBe('Version 0.1.0')
  })

  it('speaks Dutch', async () => {
    await open(answer({ version: '0.1.0', update: { version: '0.2.0', url: notes } }), 'nl')

    expect(text('h2')).toBe('Over Inkpact')
    expect(text('[data-testid="update"] h3')).toBe('Inkpact 0.2.0 is uit')
  })
})
