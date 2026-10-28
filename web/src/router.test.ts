import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, type Router } from 'vue-router'
import { createAppRouter, safeRedirect } from './router'
import { freshInstall, signedIn, signedOut, state, stubApi } from './testing/helpers'

afterEach(() => vi.unstubAllGlobals())

describe('safeRedirect', () => {
  it.each(['/', '/notes/123', '/notes/123?x=1#top', '/a/b/c'])('allows %s', (value) => {
    expect(safeRedirect(value)).toBe(value)
  })

  it.each([
    '//evil.example',
    '/\\evil.example',
    '/\\/evil.example',
    'https://evil.example',
    'http://evil.example/login',
    'javascript:alert(1)',
    'evil.example',
    '',
    '/login',
    '/login?redirect=/',
    '/setup',
    '/line\nbreak',
    '/tab\there',
    null,
    undefined,
    42,
    ['/a'],
  ])('refuses %j', (value) => {
    expect(safeRedirect(value)).toBeNull()
  })
})

describe('navigation guard', () => {
  let router: Router

  beforeEach(() => {
    setActivePinia(createPinia())
    router = createAppRouter(createMemoryHistory())
  })

  async function go(path: string) {
    await router.push(path)
    return router.currentRoute.value
  }

  it('sends everything to setup on a fresh install', async () => {
    stubApi([state(freshInstall)])

    expect((await go('/')).name).toBe('setup')
    expect((await go('/login')).name).toBe('setup')
    expect((await go('/some/page')).name).toBe('setup')
  })

  it('sends a visitor to the login page and remembers where they wanted to go', async () => {
    stubApi([state(signedOut)])

    const route = await go('/?tab=history')

    expect(route.name).toBe('login')
    expect(route.query.redirect).toBe('/?tab=history')
  })

  it('does not add a redirect for the home page', async () => {
    stubApi([state(signedOut)])

    expect((await go('/')).query).toEqual({})
  })

  it('closes the setup page once an account exists', async () => {
    stubApi([state(signedOut)])

    const route = await go('/setup')

    expect(route.name).toBe('login')
    expect(route.query).toEqual({})
  })

  it('lets a signed-in user in', async () => {
    stubApi([state(signedIn)])

    expect((await go('/')).name).toBe('home')
  })

  it('moves a signed-in user away from login and setup', async () => {
    stubApi([state(signedIn)])

    expect((await go('/login')).name).toBe('home')
    expect((await go('/setup')).name).toBe('home')
  })

  it('never follows a redirect to another site', async () => {
    stubApi([state(signedIn)])

    expect((await go('/login?redirect=//evil.example')).name).toBe('home')
    expect((await go('/login?redirect=https://evil.example')).name).toBe('home')
  })

  it('asks the server only once, until something changes', async () => {
    const fetchMock = stubApi([state(signedIn)])

    await go('/')
    await go('/login')
    await go('/')

    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('knows the import page, with and without the id of an import', async () => {
    stubApi([state(signedIn)])

    const bare = await go('/import/joplin')
    expect(bare.name).toBe('import')
    expect(bare.params.importId).toBeUndefined()

    const run = await go('/import/joplin/0198a2b4-7d3e-7c1a-9f00-5d6e7f8a9b0c')
    expect(run.name).toBe('import')
    expect(run.params.importId).toBe('0198a2b4-7d3e-7c1a-9f00-5d6e7f8a9b0c')
  })

  it('sends someone who is signed out to the login page and back to the import afterwards', async () => {
    stubApi([state(signedOut)])

    const route = await go('/import/joplin/abc')

    expect(route.name).toBe('login')
    expect(route.query.redirect).toBe('/import/joplin/abc')
  })

  it('sends an unknown address to the home page', async () => {
    stubApi([state(signedIn)])

    expect((await go('/no/such/page')).name).toBe('home')
  })
})
