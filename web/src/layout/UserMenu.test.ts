import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { createAppI18n } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { useTreeStore } from '../stores/tree'
import { fakeTree } from '../testing/treeFixtures'
import UserMenu from './UserMenu.vue'

let wrapper: VueWrapper | null = null

beforeEach(() => localStorage.clear())
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

async function menu(role: 'owner' | 'editor' | 'viewer' = 'owner') {
  const server = fakeTree({ role })
  const pinia = createPinia()
  setActivePinia(pinia)
  await useAuthStore().load()
  await useTreeStore().init()
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/login', name: 'login', component: { template: '<div />' } },
      { path: '/import/joplin/:importId?', name: 'import', component: { template: '<div />' } },
    ],
  })
  await router.push('/')
  const i18n = createAppI18n('en')
  const onNavigate = vi.fn()
  wrapper = mount(UserMenu, {
    props: { onNavigate },
    global: { plugins: [pinia, router, i18n] },
    attachTo: document.body,
  })

  const trigger = wrapper.get('button[aria-label="Account menu"]')
  const button = trigger.element as HTMLElement
  // The menu opens from the keyboard, as it does for anyone who cannot use a pointer.
  button.focus()
  await trigger.trigger('keydown', { key: 'ArrowDown' })
  await flushPromises()
  const items = () => [
    ...document.body.querySelectorAll<HTMLElement>('[role="menuitem"], [role="menuitemradio"]'),
  ]
  const item = (text: string) => items().find((el) => el.textContent?.trim().startsWith(text))!
  return { server, router, i18n, trigger, items, item, onNavigate }
}

describe('UserMenu', () => {
  it('shows who is signed in, on the button and inside the menu', async () => {
    const { trigger } = await menu()

    expect(trigger.text()).toContain('Jos')
    expect(document.body.textContent).toContain('Signed in as jos@example.com')
  })

  it('offers both languages as radio items, with the current one marked', async () => {
    const { item } = await menu()

    expect(item('English').getAttribute('aria-checked')).toBe('true')
    expect(item('Nederlands').getAttribute('aria-checked')).toBe('false')
  })

  it('switches the language, and remembers it', async () => {
    const { item, i18n } = await menu()

    item('Nederlands').click()
    await flushPromises()

    expect(i18n.global.locale.value).toBe('nl')
    expect(localStorage.getItem('inkpact.locale')).toBe('nl')
    expect(document.documentElement.lang).toBe('nl')
  })

  it('opens the Joplin import page, and tells the drawer to make way', async () => {
    const { item, router, onNavigate } = await menu()

    item('Import from Joplin').click()
    await flushPromises()

    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('import'))
    expect(onNavigate).toHaveBeenCalledOnce()
  })

  it('does not offer the import to someone who can only read', async () => {
    const { items } = await menu('viewer')

    expect(items().map((el) => el.textContent?.trim())).not.toContain('Import from Joplin')
  })

  it('signs out and goes to the login page', async () => {
    const { item, router, server } = await menu()
    server.fetch.mockClear()

    item('Sign out').click()
    await flushPromises()

    expect(server.writes().map((w) => w.path)).toEqual(['/api/auth/logout'])
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('login'))
  })

  it('signs out everywhere', async () => {
    const { items, router, server } = await menu()

    items()
      .find((el) => el.textContent?.trim() === 'Sign out everywhere')!
      .click()
    await flushPromises()

    expect(server.writes().map((w) => w.path)).toEqual(['/api/auth/logout-everywhere'])
    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('login'))
  })
})
