import { flushPromises } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  freshInstall,
  jsonResponse,
  mountAt,
  refused,
  signedIn,
  state,
  stubApi,
} from '../testing/helpers'
import SetupPage from './SetupPage.vue'

afterEach(() => vi.unstubAllGlobals())

type Wrapper = Awaited<ReturnType<typeof mountAt>>['wrapper']

async function fill(wrapper: Wrapper, values: Partial<Record<string, string>> = {}) {
  const all: Record<string, string> = {
    '#setup-code': 'ABCD2345',
    '#display-name': 'Jos',
    '#email': 'jos@example.com',
    '#password': 'correct horse battery',
    '#workspace-name': 'Private',
    ...values,
  }
  for (const [selector, value] of Object.entries(all)) await wrapper.get(selector).setValue(value)
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

describe('SetupPage', () => {
  it('explains what to do and where the setup code is', async () => {
    stubApi([state(freshInstall)])
    const { wrapper } = await mountAt(SetupPage, '/setup')

    expect(wrapper.get('h1').text()).toBe('Welcome to Inkpact')
    expect(wrapper.text()).toContain('Shown in the server log')
    expect(wrapper.get('#setup-code').attributes('autocomplete')).toBe('one-time-code')
    expect(wrapper.get('#password').attributes('autocomplete')).toBe('new-password')
    expect(wrapper.get('#email').attributes('autocomplete')).toBe('email')
    expect(wrapper.get('#display-name').attributes('autocomplete')).toBe('name')
  })

  it('links the password hint to the field', async () => {
    stubApi([state(freshInstall)])
    const { wrapper } = await mountAt(SetupPage, '/setup')

    const hintId = wrapper.get('#password').attributes('aria-describedby')!
    expect(wrapper.get(`#${hintId}`).text()).toContain('At least 12 characters')
  })

  it('suggests a first workspace name in the current language', async () => {
    stubApi([state(freshInstall)])

    const english = await mountAt(SetupPage, '/setup', 'en')
    const dutch = await mountAt(SetupPage, '/setup', 'nl')

    expect((english.wrapper.get('#workspace-name').element as HTMLInputElement).value).toBe(
      'Private',
    )
    expect((dutch.wrapper.get('#workspace-name').element as HTMLInputElement).value).toBe('Privé')
  })

  it('creates the account and goes to the home page', async () => {
    let created = false
    const fetchMock = stubApi([
      {
        method: 'GET',
        path: '/api/auth/state',
        respond: () => jsonResponse(200, created ? signedIn : freshInstall),
      },
      {
        method: 'POST',
        path: '/api/auth/setup',
        respond: () => {
          created = true
          return jsonResponse(204)
        },
      },
    ])
    const { wrapper, router } = await mountAt(SetupPage, '/setup')

    await fill(wrapper)

    const request = fetchMock.mock.calls.find(([path]) => path === '/api/auth/setup')!
    expect(JSON.parse(request[1].body)).toEqual({
      setupCode: 'ABCD2345',
      displayName: 'Jos',
      email: 'jos@example.com',
      password: 'correct horse battery',
      workspaceName: 'Private',
    })
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('home'))
  })

  it('shows an error that belongs to a field under that field', async () => {
    stubApi([
      state(freshInstall),
      refused('POST', '/api/auth/setup', 400, 'password_too_short', 'password'),
    ])
    const { wrapper } = await mountAt(SetupPage, '/setup')

    await fill(wrapper, { '#password': 'short' })

    const password = wrapper.get('#password')
    expect(password.attributes('aria-invalid')).toBe('true')
    const errorId = password.attributes('aria-describedby')!
    expect(wrapper.get(`#${errorId}`).text()).toBe('The password needs at least 12 characters.')
    expect(wrapper.find('form > [role="alert"]').exists()).toBe(false)
    expect((password.element as HTMLInputElement).value).toBe('')
  })

  it('shows the wrong setup code under its field, in Dutch too', async () => {
    stubApi([
      state(freshInstall),
      refused('POST', '/api/auth/setup', 400, 'setup_code_invalid', 'setupCode'),
    ])
    const { wrapper } = await mountAt(SetupPage, '/setup', 'nl')

    await fill(wrapper, { '#setup-code': 'WRONG' })

    const errorId = wrapper.get('#setup-code').attributes('aria-describedby')!
    expect(wrapper.get(`#${errorId}`).text()).toContain('klopt niet')
  })

  it('shows an error without a field above the form', async () => {
    stubApi([state(freshInstall), refused('POST', '/api/auth/setup', 409, 'setup_not_available')])
    const { wrapper } = await mountAt(SetupPage, '/setup')

    await fill(wrapper)

    expect(wrapper.get('[role="alert"]').text()).toBe(
      'This installation has already been set up. Sign in instead.',
    )
  })

  it('clears old errors when the form is sent again', async () => {
    stubApi([
      state(freshInstall),
      refused('POST', '/api/auth/setup', 400, 'email_invalid', 'email'),
    ])
    const { wrapper } = await mountAt(SetupPage, '/setup')
    await fill(wrapper, { '#email': 'nope' })
    expect(wrapper.get('#email').attributes('aria-invalid')).toBe('true')

    vi.stubGlobal(
      'fetch',
      vi.fn(() => new Promise(() => {})),
    )
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('#email').attributes('aria-invalid')).toBeUndefined()
  })
})
