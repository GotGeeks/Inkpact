import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { vi, type Mock } from 'vitest'
import type { Component } from 'vue'
import { createMemoryHistory } from 'vue-router'
import { createAppI18n, type Locale } from '../i18n'
import { createAppRouter } from '../router'

export function jsonResponse(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
  })
}

export interface Route {
  method: string
  path: string
  respond: () => Response | Promise<Response>
}

/** Replaces fetch with answers per method and path; an unexpected call fails the test loudly. */
export function stubApi(routes: Route[]): Mock {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? 'GET'
    const route = routes.find((r) => r.method === method && r.path === String(input))
    if (!route) throw new Error(`Unexpected request: ${method} ${String(input)}`)
    return route.respond()
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

export const signedOut = { setupRequired: false, user: null }
export const freshInstall = { setupRequired: true, user: null }
export const signedIn = {
  setupRequired: false,
  user: { id: '1', email: 'jos@example.com', displayName: 'Jos', isAdmin: true },
}

export function state(body: unknown): Route {
  return { method: 'GET', path: '/api/auth/state', respond: () => jsonResponse(200, body) }
}

/** The answer of a call that works and has no body. */
export function ok(method: string, path: string): Route {
  return { method, path, respond: () => jsonResponse(204) }
}

/** The answer of a call the server refuses, with its error code. */
export function refused(
  method: string,
  path: string,
  status: number,
  code: string,
  field?: string,
): Route {
  return { method, path, respond: () => jsonResponse(status, field ? { code, field } : { code }) }
}

/** Mounts a page with the real router, a fresh store and the real translations, at the given path. */
export async function mountAt(component: Component, path: string, locale: Locale = 'en') {
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = createAppRouter(createMemoryHistory())
  const i18n = createAppI18n(locale)
  await router.push(path)
  await router.isReady()
  const wrapper = mount(component, { global: { plugins: [pinia, i18n, router] } })
  await flushPromises()
  return { wrapper, router, i18n }
}
