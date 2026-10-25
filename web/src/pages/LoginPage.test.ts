import { flushPromises } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  jsonResponse,
  mountAt,
  ok,
  refused,
  signedIn,
  signedOut,
  state,
  stubApi,
} from '../testing/helpers'
import LoginPage from './LoginPage.vue'

afterEach(() => vi.unstubAllGlobals())

async function fillAndSubmit(
  wrapper: Awaited<ReturnType<typeof mountAt>>['wrapper'],
  email: string,
  password: string,
) {
  await wrapper.get('#email').setValue(email)
  await wrapper.get('#password').setValue(password)
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

describe('LoginPage', () => {
  it('shows a labelled form with the right autocomplete hints', async () => {
    stubApi([state(signedOut)])
    const { wrapper } = await mountAt(LoginPage, '/login')

    expect(wrapper.get('h1').text()).toBe('Sign in')
    expect(wrapper.get('label[for="email"]').text()).toBe('Email')
    expect(wrapper.get('label[for="password"]').text()).toBe('Password')
    expect(wrapper.get('#email').attributes()).toMatchObject({
      type: 'email',
      autocomplete: 'email',
      required: '',
    })
    expect(wrapper.get('#password').attributes()).toMatchObject({
      type: 'password',
      autocomplete: 'current-password',
      required: '',
    })
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
  })

  it('is in Dutch when the language is Dutch', async () => {
    stubApi([state(signedOut)])
    const { wrapper } = await mountAt(LoginPage, '/login', 'nl')

    expect(wrapper.get('h1').text()).toBe('Inloggen')
    expect(wrapper.get('label[for="password"]').text()).toBe('Wachtwoord')
  })

  it('signs in and goes to the home page', async () => {
    let signedInNow = false
    const fetchMock = stubApi([
      {
        method: 'GET',
        path: '/api/auth/state',
        respond: () => jsonResponse(200, signedInNow ? signedIn : signedOut),
      },
      {
        method: 'POST',
        path: '/api/auth/login',
        respond: () => {
          signedInNow = true
          return jsonResponse(204)
        },
      },
    ])
    const { wrapper, router } = await mountAt(LoginPage, '/login')

    await fillAndSubmit(wrapper, 'jos@example.com', 'correct horse battery')

    const login = fetchMock.mock.calls.find(([path]) => path === '/api/auth/login')!
    expect(JSON.parse(login[1].body)).toEqual({
      email: 'jos@example.com',
      password: 'correct horse battery',
    })
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('home'))
  })

  it('shows a translated error, announced, and clears the password', async () => {
    stubApi([state(signedOut), refused('POST', '/api/auth/login', 401, 'invalid_credentials')])
    const { wrapper, router } = await mountAt(LoginPage, '/login')

    await fillAndSubmit(wrapper, 'jos@example.com', 'wrong password!')

    const alert = wrapper.get('[role="alert"]')
    expect(alert.text()).toBe('The email or password is not right.')
    expect((wrapper.get('#password').element as HTMLInputElement).value).toBe('')
    expect((wrapper.get('#email').element as HTMLInputElement).value).toBe('jos@example.com')
    expect(router.currentRoute.value.name).toBe('login')
  })

  it('shows the error in Dutch', async () => {
    stubApi([state(signedOut), refused('POST', '/api/auth/login', 401, 'invalid_credentials')])
    const { wrapper } = await mountAt(LoginPage, '/login', 'nl')

    await fillAndSubmit(wrapper, 'jos@example.com', 'wrong password!')

    expect(wrapper.get('[role="alert"]').text()).toBe('Het e-mailadres of wachtwoord klopt niet.')
  })

  it('clears the error on the next attempt', async () => {
    stubApi([state(signedOut), refused('POST', '/api/auth/login', 401, 'invalid_credentials')])
    const { wrapper } = await mountAt(LoginPage, '/login')
    await fillAndSubmit(wrapper, 'jos@example.com', 'wrong password!')
    expect(wrapper.find('[role="alert"]').exists()).toBe(true)

    vi.stubGlobal(
      'fetch',
      vi.fn(() => new Promise(() => {})),
    )
    await wrapper.get('#password').setValue('another try 123')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
  })

  it('says so when the server cannot be reached', async () => {
    stubApi([state(signedOut)])
    const { wrapper } = await mountAt(LoginPage, '/login')
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    await fillAndSubmit(wrapper, 'jos@example.com', 'correct horse battery')

    expect(wrapper.get('[role="alert"]').text()).toContain('Could not reach the server')
  })

  it('falls back to a generic message for a code it does not know', async () => {
    stubApi([state(signedOut), refused('POST', '/api/auth/login', 400, 'some_future_code')])
    const { wrapper } = await mountAt(LoginPage, '/login')

    await fillAndSubmit(wrapper, 'jos@example.com', 'correct horse battery')

    expect(wrapper.get('[role="alert"]').text()).toBe('Something went wrong. Try again.')
  })

  it('disables the button and ignores a second submit while a request runs', async () => {
    let finish: (response: Response) => void = () => {}
    const fetchMock = stubApi([
      state(signedOut),
      {
        method: 'POST',
        path: '/api/auth/login',
        respond: () => new Promise<Response>((resolve) => (finish = resolve)),
      },
    ])
    const { wrapper } = await mountAt(LoginPage, '/login')
    await wrapper.get('#email').setValue('jos@example.com')
    await wrapper.get('#password').setValue('correct horse battery')

    await wrapper.get('form').trigger('submit')
    await wrapper.get('form').trigger('submit')

    const button = wrapper.get('button[type="submit"]')
    expect(button.attributes('disabled')).toBeDefined()
    expect(button.attributes('aria-busy')).toBe('true')
    expect(fetchMock.mock.calls.filter(([path]) => path === '/api/auth/login')).toHaveLength(1)

    finish(ok('POST', '/api/auth/login').respond() as Response)
    await flushPromises()
  })

  it('says how long to wait when the account is locked', async () => {
    stubApi([
      state(signedOut),
      {
        method: 'POST',
        path: '/api/auth/login',
        respond: () =>
          new Response(JSON.stringify({ code: 'login_locked' }), {
            status: 429,
            headers: { 'Content-Type': 'application/json', 'Retry-After': '240' },
          }),
      },
    ])
    const { wrapper } = await mountAt(LoginPage, '/login')

    await fillAndSubmit(wrapper, 'jos@example.com', 'correct horse battery')

    expect(wrapper.get('[role="alert"]').text()).toBe(
      'Too many wrong passwords for this account. Try again in 4 minutes.',
    )
  })

  it('says it in Dutch too', async () => {
    stubApi([
      state(signedOut),
      {
        method: 'POST',
        path: '/api/auth/login',
        respond: () =>
          new Response(JSON.stringify({ code: 'too_many_requests' }), {
            status: 429,
            headers: { 'Content-Type': 'application/json', 'Retry-After': '30' },
          }),
      },
    ])
    const { wrapper } = await mountAt(LoginPage, '/login', 'nl')

    await fillAndSubmit(wrapper, 'jos@example.com', 'correct horse battery')

    expect(wrapper.get('[role="alert"]').text()).toBe(
      'Te veel pogingen vanaf dit apparaat. Probeer het opnieuw over 30 seconden.',
    )
  })
})
