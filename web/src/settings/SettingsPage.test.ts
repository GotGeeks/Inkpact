import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n, type Locale } from '../i18n'
import { useTreeStore } from '../stores/tree'
import { jsonResponse } from '../testing/helpers'
import type { HistoryPolicy } from './historyPolicy'
import SettingsPage from './SettingsPage.vue'

const workspace = { id: 'w1', name: 'Private' }
const policyPath = `/api/workspaces/${workspace.id}/history-policy`
const usagePath = `/api/workspaces/${workspace.id}/history-usage`
const defaults: HistoryPolicy = {
  enabled: true,
  granularHours: 24,
  dailyDays: 30,
  expiryDays: 365,
  capMiB: null,
}

let wrapper: VueWrapper | null = null
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

/** The server: the workspace with the person's role, its policy, what it takes, and what a save answers. */
function server(
  role: 'owner' | 'editor' | 'viewer',
  options: { policy?: HistoryPolicy; put?: () => Response; failPolicy?: boolean } = {},
) {
  let policy = options.policy ?? defaults
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input)
    const method = init?.method ?? 'GET'
    if (path === '/api/workspaces') return jsonResponse(200, [{ ...workspace, role }])
    if (path === `/api/workspaces/${workspace.id}/pages`)
      return jsonResponse(200, { treeVersion: 1, pages: [] })
    if (path === `/api/workspaces/${workspace.id}/trash`) return jsonResponse(200, [])
    if (path === policyPath && method === 'GET')
      return options.failPolicy
        ? jsonResponse(500, { code: 'server_error' })
        : jsonResponse(200, policy)
    if (path === usagePath) return jsonResponse(200, { usedBytes: 3_400_000, protectedBytes: 0 })
    if (path === policyPath && method === 'PUT') {
      if (options.put) return options.put()
      policy = JSON.parse(String(init!.body)) as HistoryPolicy
      return jsonResponse(200, policy)
    }
    throw new Error(`Unexpected request: ${method} ${path}`)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

async function open(
  role: 'owner' | 'editor' | 'viewer',
  options: Parameters<typeof server>[1] = {},
  locale: Locale = 'en',
) {
  const fetchMock = server(role, options)
  const pinia = createPinia()
  setActivePinia(pinia)
  await useTreeStore().init()
  wrapper = mount(SettingsPage, {
    global: { plugins: [pinia, createAppI18n(locale)] },
    attachTo: document.body,
  })
  await flushPromises()
  return fetchMock
}

const field = (id: string) => document.getElementById(id) as HTMLInputElement
const text = (element: Element | null) => element?.textContent?.replace(/\s+/g, ' ').trim() ?? ''
const byTestId = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid="${id}"]`)
const buttonNamed = (name: string) =>
  [...document.body.querySelectorAll('button')].find((b) => text(b) === name)
async function type(id: string, value: string) {
  const input = field(id)
  input.value = value
  input.dispatchEvent(new Event('input'))
  await flushPromises()
}
async function submit() {
  document.body.querySelector('form')!.dispatchEvent(new Event('submit'))
  await flushPromises()
}
const puts = (fetchMock: ReturnType<typeof server>) =>
  fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')

describe('SettingsPage', () => {
  it('shows the workspace’s history policy and what it takes', async () => {
    await open('owner')

    expect(text(document.querySelector('h1'))).toBe('Settings')
    expect(text(document.querySelector('h2'))).toBe('History of this workspace')
    expect((document.querySelector('input[type="checkbox"]') as HTMLInputElement).checked).toBe(
      true,
    )
    expect([
      field('granular-hours').value,
      field('daily-days').value,
      field('expiry-days').value,
    ]).toEqual(['24', '30', '365'])
    expect(field('cap-mib')).toBeNull()
    expect(text(byTestId('history-usage'))).toBe(
      `Versions take ${new Intl.NumberFormat('en', { style: 'unit', unit: 'megabyte', unitDisplay: 'short', maximumFractionDigits: 1 }).format(3.4)}.`,
    )
  })

  it('lets the owner save longer windows and a limit without asking, and says it was saved', async () => {
    const fetchMock = await open('owner', { policy: { ...defaults, capMiB: 100 } })
    await type('granular-hours', '48')
    await type('cap-mib', '200')

    await submit()

    expect(puts(fetchMock)).toHaveLength(1)
    expect(JSON.parse(String(puts(fetchMock)[0]![1]!.body))).toEqual({
      ...defaults,
      granularHours: 48,
      capMiB: 200,
    })
    expect(text(document.querySelector('[role="status"]'))).toBe('Saved.')
  })

  it('warns before a save that removes versions, and saves only when the owner says so', async () => {
    const fetchMock = await open('owner')
    await type('expiry-days', '90')

    await submit()

    expect(puts(fetchMock)).toHaveLength(0)
    const warning = byTestId('policy-warning')!
    expect(text(warning)).toContain(
      'Shorter windows or a lower limit remove the versions that no longer fit',
    )
    expect(document.activeElement).toBe(warning.querySelector('h3'))

    buttonNamed('Cancel')!.click()
    await flushPromises()
    expect(byTestId('policy-warning')).toBeNull()
    expect(puts(fetchMock)).toHaveLength(0)

    await submit()
    buttonNamed('Save and remove')!.click()
    await flushPromises()
    expect(puts(fetchMock)).toHaveLength(1)
    expect(JSON.parse(String(puts(fetchMock)[0]![1]!.body))).toMatchObject({ expiryDays: 90 })
  })

  it('says what is wrong with the numbers, and sends nothing', async () => {
    const fetchMock = await open('owner')
    await type('daily-days', '0.5')

    await submit()

    expect(text(byTestId('policy-problem'))).toBe(
      'Keep one a day for a whole number of days, from 1 to 3650.',
    )
    expect(puts(fetchMock)).toHaveLength(0)

    await type('daily-days', '30')
    await type('expiry-days', '10')
    await submit()
    expect(text(byTestId('policy-problem'))).toBe(
      'One a week must last at least as long as one a day.',
    )
  })

  it('says when the limit is below what protected versions take', async () => {
    await open('owner', { put: () => jsonResponse(400, { code: 'history_cap_below_protected' }) })
    ;(document.querySelectorAll('input[type="checkbox"]')[1] as HTMLInputElement).click()
    await flushPromises()
    await type('cap-mib', '1000')

    await submit()
    // A limit where there was none can remove versions: the warning comes first.
    buttonNamed('Save and remove')!.click()
    await flushPromises()

    expect(text(byTestId('save-error'))).toContain(
      'The limit is lower than what protected versions take',
    )
  })

  it.each(['editor', 'viewer'] as const)(
    'shows a %s the policy, and nothing to change it with',
    async (role) => {
      await open(role)

      expect(text(byTestId('owner-only'))).toBe('Only the owner of the workspace can change these.')
      expect(field('granular-hours').closest('fieldset')!.disabled).toBe(true)
      expect((document.querySelector('input[type="checkbox"]') as HTMLInputElement).disabled).toBe(
        true,
      )
      expect(document.body.querySelector('button[type="submit"]')).toBeNull()
    },
  )

  it('says when the policy could not be loaded, and loads it when asked again', async () => {
    const fetchMock = await open('owner', { failPolicy: true })

    expect(text(document.querySelector('[role="alert"]'))).toContain(
      'The history settings could not be loaded.',
    )
    fetchMock.mockClear()
    vi.stubGlobal('fetch', server('owner'))
    buttonNamed('Try again')!.click()
    await flushPromises()
    expect(field('granular-hours').value).toBe('24')
  })

  it('speaks Dutch', async () => {
    await open('owner', {}, 'nl')

    expect(text(document.querySelector('h2'))).toBe('Geschiedenis van deze werkruimte')
  })
})
