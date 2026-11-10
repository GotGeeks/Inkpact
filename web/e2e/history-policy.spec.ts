import { request as playwrightRequest } from '@playwright/test'
import { SIGNED_OUT } from './credentials'
import { expect, test } from './fixtures'

interface HistoryPolicy {
  enabled: boolean
  granularHours: number
  dailyDays: number
  expiryDays: number
  capMiB: number | null
}

test('history policy guards work and the owner can save settings on desktop', async ({
  request,
  baseURL,
}, testInfo) => {
  const stateResponse = await request.get('/api/auth/state')
  expect(stateResponse.status()).toBe(200)
  const state = (await stateResponse.json()) as { csrfToken: string }
  const headers = { 'X-CSRF-Token': state.csrfToken }

  const workspacesResponse = await request.get('/api/workspaces')
  expect(workspacesResponse.status()).toBe(200)
  const workspaces = (await workspacesResponse.json()) as { id: string }[]
  expect(workspaces).toHaveLength(1)
  const path = `/api/workspaces/${workspaces[0]!.id}/history-policy`

  const response = await request.get(path)
  expect(response.status()).toBe(200)
  const policy = (await response.json()) as HistoryPolicy
  expect(policy.enabled).toEqual(expect.any(Boolean))
  expect(policy.granularHours).toEqual(expect.any(Number))
  expect(policy.dailyDays).toEqual(expect.any(Number))
  expect(policy.expiryDays).toEqual(expect.any(Number))
  expect(policy.capMiB === null || (Number.isSafeInteger(policy.capMiB) && policy.capMiB > 0)).toBe(
    true,
  )

  const invalid = await request.put(path, {
    headers,
    data: { ...policy, granularHours: 0 },
  })
  expect(invalid.status()).toBe(400)
  expect(await invalid.json()).toEqual({ code: 'history_policy_invalid' })

  const withoutCsrf = await request.put(path, { data: policy })
  expect(withoutCsrf.status()).toBe(400)
  expect(await withoutCsrf.json()).toEqual({ code: 'csrf_invalid' })

  const unknown = await request.get(
    '/api/workspaces/11111111-1111-1111-1111-111111111111/history-policy',
  )
  expect(unknown.status()).toBe(404)
  expect(await unknown.json()).toEqual({ code: 'not_found' })

  const stranger = await playwrightRequest.newContext({ baseURL, storageState: SIGNED_OUT })
  try {
    const anonymous = await stranger.get(path)
    expect(anonymous.status()).toBe(401)
    expect(await anonymous.json()).toEqual({ code: 'not_authenticated' })
  } finally {
    await stranger.dispose()
  }

  // The device projects share a workspace; only one changes its policy.
  if (testInfo.project.name === 'desktop') {
    const changed: HistoryPolicy = {
      enabled: !policy.enabled,
      granularHours: 48,
      dailyDays: 60,
      expiryDays: 730,
      capMiB: 8,
    }
    try {
      const saved = await request.put(path, { headers, data: changed })
      expect(saved.status()).toBe(200)
      expect(await saved.json()).toEqual(changed)
      const reread = await request.get(path)
      expect(reread.status()).toBe(200)
      expect(await reread.json()).toEqual(changed)

      const rejected = await request.put(path, {
        headers,
        data: { ...changed, granularHours: 0 },
      })
      expect(rejected.status()).toBe(400)
      const unchanged = await request.get(path)
      expect(await unchanged.json()).toEqual(changed)
    } finally {
      const restored = await request.put(path, { headers, data: policy })
      expect(restored.status()).toBe(200)
    }
  }
})
