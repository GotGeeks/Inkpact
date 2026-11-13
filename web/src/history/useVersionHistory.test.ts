import { afterEach, describe, expect, it, vi } from 'vitest'
import { jsonResponse } from '../testing/helpers'
import type { VersionList, VersionSummary } from './types'
import { useVersionHistory } from './useVersionHistory'

const pageId = '0a1b2c3d-0000-4000-8000-00000000000a'

function version(id: string, minutesAgo = 0): VersionSummary {
  return {
    id,
    createdAt: new Date(Date.UTC(2026, 10, 12, 12, 0) - minutesAgo * 60_000).toISOString(),
    kind: 'automatic',
    title: 'Page',
    createdBy: null,
    bytes: 100,
  }
}

function listOf(versions: VersionSummary[], next: string | null = null): VersionList {
  return { versions, next, state: 'ready', failure: null, pageContent: '7.3' }
}

const detailOf = (id: string) => ({
  id,
  createdAt: version(id).createdAt,
  kind: 'automatic',
  title: 'Then',
  content: { type: 'doc' },
})

/** Answers the requests in the order they are asked, each when the test says so. */
function controlledFetch() {
  const calls: {
    url: string
    resolve: (response: Response) => void
    reject: (e: unknown) => void
  }[] = []
  const fetchMock = vi.fn(
    (input: RequestInfo | URL) =>
      new Promise<Response>((resolve, reject) =>
        calls.push({ url: String(input), resolve, reject }),
      ),
  )
  vi.stubGlobal('fetch', fetchMock)
  return { calls, fetchMock }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('useVersionHistory', () => {
  it('reads the first page of versions, newest first as the server gives them, and where the history stands', async () => {
    const { calls } = controlledFetch()
    const history = useVersionHistory(pageId)

    const loading = history.load()
    expect(history.list.loading).toBe(true)
    expect(calls[0]!.url).toBe(`/api/pages/${pageId}/versions?limit=50`)
    calls[0]!.resolve(
      jsonResponse(200, {
        ...listOf([version('v2'), version('v1', 5)], 'cursor-1'),
        state: 'pending',
      }),
    )
    await loading

    expect(history.list.loading).toBe(false)
    expect(history.list.loaded).toBe(true)
    expect(history.list.versions.map((v) => v.id)).toEqual(['v2', 'v1'])
    expect(history.list.next).toBe('cursor-1')
    expect(history.list.state).toBe('pending')
  })

  it('reads older versions with the cursor, adds them after the others once, and stops when there are none', async () => {
    const { calls } = controlledFetch()
    const history = useVersionHistory(pageId)
    const first = history.load()
    calls[0]!.resolve(jsonResponse(200, listOf([version('v3'), version('v2', 1)], 'c/1')))
    await first

    const more = history.loadMore()
    // A second press while the first is on its way asks nothing.
    void history.loadMore()
    expect(calls).toHaveLength(2)
    expect(calls[1]!.url).toBe(`/api/pages/${pageId}/versions?limit=50&before=c%2F1`)
    // v2 was made again at the edge of the two pages while the person read: shown once.
    calls[1]!.resolve(jsonResponse(200, listOf([version('v2', 1), version('v1', 2)])))
    await more

    expect(history.list.versions.map((v) => v.id)).toEqual(['v3', 'v2', 'v1'])
    expect(history.list.next).toBeNull()
    await history.loadMore()
    expect(calls).toHaveLength(2)
  })

  it('keeps the versions it has when older ones cannot be read, and says so until they can', async () => {
    const { calls } = controlledFetch()
    const history = useVersionHistory(pageId)
    const first = history.load()
    calls[0]!.resolve(jsonResponse(200, listOf([version('v2')], 'c1')))
    await first

    const failing = history.loadMore()
    calls[1]!.reject(new TypeError('offline'))
    await failing
    expect(history.list.moreError).toBe('network_error')
    expect(history.list.versions.map((v) => v.id)).toEqual(['v2'])
    expect(history.list.next).toBe('c1')

    const again = history.loadMore()
    expect(history.list.moreError).toBeNull()
    calls[2]!.resolve(jsonResponse(200, listOf([version('v1', 3)])))
    await again
    expect(history.list.versions.map((v) => v.id)).toEqual(['v2', 'v1'])
  })

  it('says why the first page could not be read, and reads it when asked again', async () => {
    const { calls } = controlledFetch()
    const history = useVersionHistory(pageId)

    const failing = history.load()
    calls[0]!.resolve(jsonResponse(500, { code: 'server_error' }))
    await failing
    expect(history.list.error).toBe('server_error')
    expect(history.list.loaded).toBe(false)

    const again = history.load()
    expect(history.list.error).toBeNull()
    calls[1]!.resolve(jsonResponse(200, listOf([version('v1')])))
    await again
    expect(history.list.loaded).toBe(true)
  })

  it('drops older versions that arrive after the list was read again', async () => {
    const { calls } = controlledFetch()
    const history = useVersionHistory(pageId)
    const first = history.load()
    calls[0]!.resolve(jsonResponse(200, listOf([version('v3')], 'c1')))
    await first

    const more = history.loadMore()
    const again = history.load()
    expect(history.list.loadingMore).toBe(false)
    calls[2]!.resolve(jsonResponse(200, listOf([version('v4'), version('v3')], 'c2')))
    await again
    calls[1]!.resolve(jsonResponse(200, listOf([version('v1', 9)])))
    await more

    expect(history.list.versions.map((v) => v.id)).toEqual(['v4', 'v3'])
    expect(history.list.next).toBe('c2')
  })

  it('reads a version once and keeps it, also when it is opened again', async () => {
    const { calls } = controlledFetch()
    const history = useVersionHistory(pageId)

    const opening = history.open('v1')
    expect(history.details.get('v1')).toEqual({ status: 'loading' })
    // Asked again while on its way: one request.
    void history.open('v1')
    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe(`/api/pages/${pageId}/versions/v1`)
    calls[0]!.resolve(jsonResponse(200, detailOf('v1')))
    await opening

    expect(history.details.get('v1')).toEqual({ status: 'ready', detail: detailOf('v1') })
    await history.open('v1')
    expect(calls).toHaveLength(1)
  })

  it('finishes reading a version when the list is read again meanwhile', async () => {
    const { calls } = controlledFetch()
    const history = useVersionHistory(pageId)

    const opening = history.open('v1')
    const reading = history.load()
    calls[1]!.resolve(jsonResponse(200, listOf([version('v1')])))
    await reading
    calls[0]!.resolve(jsonResponse(200, detailOf('v1')))
    await opening

    expect(history.details.get('v1')?.status).toBe('ready')
  })

  it('restores against the page as the list saw it, under one operation id, one request at a time', async () => {
    const { calls } = controlledFetch()
    const fetchMock = vi.mocked(fetch)
    const history = useVersionHistory(pageId)
    const reading = history.load()
    calls[0]!.resolve(jsonResponse(200, listOf([version('v1')])))
    await reading

    const restoring = history.restore('v1', 5)
    expect(history.restoring.busy).toBe(true)
    // A second press meanwhile asks nothing.
    expect(await history.restore('v1', 5)).toBeNull()
    expect(calls).toHaveLength(2)
    expect(calls[1]!.url).toBe(`/api/pages/${pageId}/versions/v1/restore`)
    const body = JSON.parse(String(fetchMock.mock.calls[1]![1]!.body)) as Record<string, unknown>
    expect(body).toEqual({
      operationId: expect.any(String),
      expectedContent: '7.3',
      expectedVersion: 5,
    })
    const answer = { beforeVersionId: 'b', restoreVersionId: 'r', title: 'Then', version: 6 }
    calls[1]!.resolve(jsonResponse(200, answer))

    expect(await restoring).toEqual(answer)
    expect(history.restoring.busy).toBe(false)
    expect(history.restoring.error).toBeNull()
  })

  it('asks again under the same operation id when the answer was lost, and under a new one after a refusal', async () => {
    const { calls } = controlledFetch()
    const fetchMock = vi.mocked(fetch)
    const history = useVersionHistory(pageId)
    const operationOf = (call: number) =>
      (JSON.parse(String(fetchMock.mock.calls[call]![1]!.body)) as { operationId: string })
        .operationId

    const lost = history.restore('v1', 1)
    calls[0]!.reject(new TypeError('offline'))
    expect(await lost).toBeNull()
    expect(history.restoring.error).toBe('network_error')

    const again = history.restore('v1', 1)
    expect(operationOf(1)).toBe(operationOf(0))
    calls[1]!.resolve(jsonResponse(409, { code: 'page_changed' }))
    expect(await again).toBeNull()
    expect(history.restoring.error).toBe('page_changed')

    void history.restore('v1', 1)
    expect(operationOf(2)).not.toBe(operationOf(0))
  })

  it('says why a version could not be read, and reads it again when asked', async () => {
    const { calls } = controlledFetch()
    const history = useVersionHistory(pageId)

    const failing = history.open('v1')
    calls[0]!.resolve(jsonResponse(503, { code: 'history_unavailable' }))
    await failing
    expect(history.details.get('v1')).toEqual({ status: 'error', code: 'history_unavailable' })

    const again = history.open('v1')
    expect(calls).toHaveLength(2)
    calls[1]!.resolve(jsonResponse(200, detailOf('v1')))
    await again
    expect(history.details.get('v1')?.status).toBe('ready')
  })
})
