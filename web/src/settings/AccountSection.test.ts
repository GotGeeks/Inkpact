import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { jsonResponse } from '../testing/helpers'
import AccountSection from './AccountSection.vue'

const firefoxOnWindows =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:145.0) Gecko/20100101 Firefox/145.0'
const safariOnIPhone =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 19_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.0 Mobile/15E148 Safari/604.1'

const now = new Date()
const minutesAgo = (minutes: number) => new Date(now.getTime() - minutes * 60_000).toISOString()
const thisOne = {
  id: 'this',
  signedInAt: '2026-11-01T10:00:00Z',
  lastSeenAt: minutesAgo(0),
  userAgent: firefoxOnWindows,
  current: true,
}
const phone = {
  id: 'phone',
  signedInAt: '2026-11-10T08:00:00Z',
  lastSeenAt: minutesAgo(180),
  userAgent: safariOnIPhone,
  current: false,
}
const unknown = { ...phone, id: 'old', userAgent: null }

let wrapper: VueWrapper | null = null
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

type Answer = (init?: RequestInit) => Response | Promise<Response>

/** The server: the sessions it lists, and what each write answers. Calls are kept, in order. */
function server(routes: Record<string, Answer>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${String(input)}`
    const answer = routes[key]
    if (!answer) throw new Error(`Unexpected request: ${key}`)
    return answer(init)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

async function open(routes: Record<string, Answer>, locale: Locale = 'en') {
  const fetchMock = server(routes)
  const pinia = createPinia()
  setActivePinia(pinia)
  useAuthStore().user = { id: 'u1', email: 'jos@example.com', displayName: 'Jos', isAdmin: true }
  wrapper = mount(AccountSection, {
    global: { plugins: [pinia, createAppI18n(locale)] },
    attachTo: document.body,
  })
  await flushPromises()
  return fetchMock
}

const listed =
  (...sessions: unknown[]) =>
  () =>
    jsonResponse(200, { sessions, more: 0 })
const text = (element: Element | null | undefined) =>
  element?.textContent?.replace(/\s+/g, ' ').trim() ?? ''
const byTestId = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`)
const devices = () => [...document.body.querySelectorAll<HTMLElement>('[data-testid="device"]')]
const buttonIn = (root: Element, name: string) =>
  [...root.querySelectorAll('button')].find((b) => text(b) === name)
const writes = (fetchMock: ReturnType<typeof server>) =>
  fetchMock.mock.calls
    .filter(([, init]) => (init?.method ?? 'GET') !== 'GET')
    .map(([input, init]) => `${init!.method} ${String(input)}`)

describe('AccountSection', () => {
  describe('the display name', () => {
    it('is shown, saved, and is the menu’s name at once', async () => {
      const fetchMock = await open({
        'GET /api/account/sessions': listed(thisOne),
        'PATCH /api/account': (init) => {
          const { displayName } = JSON.parse(String(init!.body)) as { displayName: string }
          return jsonResponse(200, {
            id: 'u1',
            email: 'jos@example.com',
            displayName: displayName.trim(),
            isAdmin: true,
          })
        },
      })
      const field = document.getElementById('display-name') as HTMLInputElement
      expect(field.value).toBe('Jos')

      field.value = '  Jos N.  '
      field.dispatchEvent(new Event('input'))
      document.body.querySelector('form')!.dispatchEvent(new Event('submit'))
      await flushPromises()

      expect(writes(fetchMock)).toEqual(['PATCH /api/account'])
      expect(useAuthStore().user?.displayName).toBe('Jos N.')
      expect(field.value).toBe('Jos N.')
      expect(text(document.body.querySelector('form [role="status"]'))).toBe('Saved.')
    })

    it('says what is wrong with a name it cannot take', async () => {
      await open({
        'GET /api/account/sessions': listed(thisOne),
        'PATCH /api/account': () =>
          jsonResponse(400, { code: 'display_name_invalid', field: 'displayName' }),
      })
      const field = document.getElementById('display-name') as HTMLInputElement
      field.value = ''
      field.dispatchEvent(new Event('input'))

      document.body.querySelector('form')!.dispatchEvent(new Event('submit'))
      await flushPromises()

      expect(text(document.getElementById('display-name-error'))).toBe(
        'Enter your name, up to 200 characters.',
      )
      expect(field.getAttribute('aria-invalid')).toBe('true')
      expect(useAuthStore().user?.displayName).toBe('Jos')
    })
  })

  describe('the devices', () => {
    it('lists each with a readable name, when it signed in and was last active, and this one marked', async () => {
      await open({ 'GET /api/account/sessions': listed(thisOne, phone, unknown) })

      const [mine, theirs, old] = devices()
      expect(text(mine!.querySelector('p'))).toBe('Firefox on Windows This device')
      expect(mine!.querySelector('button')).toBeNull()
      expect(text(theirs!.querySelector('p'))).toBe('Safari on iOS')
      expect(theirs!.querySelector('p')!.getAttribute('title')).toBe(safariOnIPhone)
      expect(text(theirs!.querySelectorAll('p')[1]!)).toBe(
        `Signed in ${new Intl.DateTimeFormat('en', { dateStyle: 'medium' }).format(new Date(phone.signedInAt))} · last active 3 hours ago`,
      )
      expect(text(old!.querySelector('p'))).toBe('Unknown browser on an unknown system')
      expect(text(byTestId('devices'))).toContain('Pages it saved for offline use stay on it')
    })

    it('asks before signing a device out, signs it out, and reads the list again', async () => {
      let signedOut = false
      const fetchMock = await open({
        'GET /api/account/sessions': () =>
          jsonResponse(200, { sessions: signedOut ? [thisOne] : [thisOne, phone], more: 0 }),
        'DELETE /api/account/sessions/phone': () => {
          signedOut = true
          return new Response(null, { status: 204 })
        },
      })
      const theirs = devices()[1]!

      buttonIn(theirs, 'Sign out')!.click()
      await flushPromises()
      expect(document.activeElement).toBe(document.getElementById('confirm-phone'))
      expect(writes(fetchMock)).toEqual([])

      buttonIn(theirs, 'Sign out')!.click()
      await flushPromises()

      expect(writes(fetchMock)).toEqual(['DELETE /api/account/sessions/phone'])
      expect(devices()).toHaveLength(1)
      expect(text(byTestId('devices-done'))).toBe('Safari on iOS is signed out.')
      expect(document.activeElement?.tagName).toBe('H3')
    })

    it('goes back to the button when the person thinks again', async () => {
      const fetchMock = await open({ 'GET /api/account/sessions': listed(thisOne, phone) })
      buttonIn(devices()[1]!, 'Sign out')!.click()
      await flushPromises()

      buttonIn(devices()[1]!, 'Cancel')!.click()
      await flushPromises()

      expect(document.activeElement).toBe(document.getElementById('sign-out-phone'))
      expect(writes(fetchMock)).toEqual([])
    })

    it('says so when the device was already signed out', async () => {
      await open({
        'GET /api/account/sessions': listed(thisOne, phone),
        'DELETE /api/account/sessions/phone': () =>
          jsonResponse(404, { code: 'session_not_found' }),
      })
      buttonIn(devices()[1]!, 'Sign out')!.click()
      await flushPromises()
      buttonIn(devices()[1]!, 'Sign out')!.click()
      await flushPromises()

      expect(text(byTestId('devices-done'))).toBe('That device was already signed out.')
    })

    it('signs out all the other devices after asking, and says how many', async () => {
      let signedOut = false
      const fetchMock = await open({
        'GET /api/account/sessions': () =>
          jsonResponse(200, {
            sessions: signedOut ? [thisOne] : [thisOne, phone, unknown],
            more: 0,
          }),
        'POST /api/account/sessions/sign-out-others': () => {
          signedOut = true
          return jsonResponse(200, { ended: 2 })
        },
      })
      const section = byTestId('devices')!

      buttonIn(section, 'Sign out all other devices')!.click()
      await flushPromises()
      expect(text(document.getElementById('confirm-others'))).toContain(
        'Sign out every device but this one?',
      )
      buttonIn(section, 'Sign out all other devices')!.click()
      await flushPromises()

      expect(writes(fetchMock)).toEqual(['POST /api/account/sessions/sign-out-others'])
      expect(text(byTestId('devices-done'))).toBe('2 other devices are signed out.')
      expect(devices()).toHaveLength(1)
      expect(buttonIn(byTestId('devices')!, 'Sign out all other devices')).toBeUndefined()
    })

    it('counts the devices it does not show, and still offers to sign them out', async () => {
      await open({
        'GET /api/account/sessions': () => jsonResponse(200, { sessions: [thisOne], more: 3 }),
      })

      expect(text(byTestId('devices'))).toContain('And 3 more, not shown.')
      expect(buttonIn(byTestId('devices')!, 'Sign out all other devices')).toBeDefined()
    })

    it('says when the list could not be read, and reads it when asked again', async () => {
      let fail = true
      const fetchMock = await open({
        'GET /api/account/sessions': () =>
          fail ? jsonResponse(500, { code: 'server_error' }) : listed(thisOne)(),
      })
      expect(text(byTestId('devices')!.querySelector('[role="alert"]'))).toContain(
        'The list of devices could not be loaded.',
      )

      fail = false
      buttonIn(byTestId('devices')!, 'Try again')!.click()
      await flushPromises()

      expect(fetchMock).toHaveBeenCalledTimes(2)
      expect(devices()).toHaveLength(1)
    })
  })

  it('speaks Dutch', async () => {
    await open({ 'GET /api/account/sessions': listed(thisOne, phone) }, 'nl')

    expect(text(document.querySelector('h2'))).toBe('Je account')
    expect(text(devices()[1]!.querySelector('p'))).toBe('Safari op iOS')
    expect(text(devices()[0]!.querySelector('p'))).toBe('Firefox op Windows Dit apparaat')
  })
})
