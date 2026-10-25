import { flushPromises } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { jsonResponse, mountAt, signedIn, signedOut, state, stubApi } from '../testing/helpers'
import HomePage from './HomePage.vue'

afterEach(() => vi.unstubAllGlobals())

describe('HomePage', () => {
  it('greets the signed-in user by name', async () => {
    stubApi([state(signedIn)])
    const { wrapper } = await mountAt(HomePage, '/')

    expect(wrapper.get('h1').text()).toBe('Signed in as Jos')
  })

  it('greets in Dutch', async () => {
    stubApi([state(signedIn)])
    const { wrapper } = await mountAt(HomePage, '/', 'nl')

    expect(wrapper.get('h1').text()).toBe('Ingelogd als Jos')
  })

  it('does not treat the name as markup', async () => {
    stubApi([
      state({
        ...signedIn,
        user: { ...signedIn.user, displayName: '<img src=x onerror=alert(1)>' },
      }),
    ])
    const { wrapper } = await mountAt(HomePage, '/')

    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.get('h1').text()).toContain('<img src=x onerror=alert(1)>')
  })

  it.each([
    ['Sign out', '/api/auth/logout'],
    ['Sign out everywhere', '/api/auth/logout-everywhere'],
  ])('%s ends the session and goes to the login page', async (label, path) => {
    let signedInNow = true
    const fetchMock = stubApi([
      {
        method: 'GET',
        path: '/api/auth/state',
        respond: () => jsonResponse(200, signedInNow ? signedIn : signedOut),
      },
      {
        method: 'POST',
        path,
        respond: () => {
          signedInNow = false
          return jsonResponse(204)
        },
      },
    ])
    const { wrapper, router } = await mountAt(HomePage, '/')

    await wrapper
      .findAll('button')
      .find((b) => b.text() === label)!
      .trigger('click')
    await flushPromises()

    expect(fetchMock.mock.calls.some(([p, init]) => p === path && init.method === 'POST')).toBe(
      true,
    )
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('login'))
  })

  it('has real buttons that can be reached with the keyboard', async () => {
    stubApi([state(signedIn)])
    const { wrapper } = await mountAt(HomePage, '/')

    const buttons = wrapper
      .findAll('button')
      .filter((b) => ['Sign out', 'Sign out everywhere'].includes(b.text()))
    expect(buttons).toHaveLength(2)
    for (const button of buttons) expect(button.attributes('type')).toBe('button')
  })
})
