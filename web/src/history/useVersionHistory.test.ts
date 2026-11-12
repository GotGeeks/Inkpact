import { effectScope, nextTick, ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { jsonResponse } from '../testing/helpers'
import type { VersionList, VersionSummary } from './types'
import { useVersionHistory } from './useVersionHistory'

const pageA = '0a1b2c3d-0000-4000-8000-00000000000a'
const pageB = '0a1b2c3d-0000-4000-8000-00000000000b'

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
  return { versions, next, state: 'ready', failure: null }
}

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

const flush = async () => {
  for (let i = 0; i < 5; i++) await Promise.resolve()
  await nextTick()
}

let stop: () => void = () => {}

function start(pageId = pageA) {
  const id = ref(pageId)
  const scope = effectScope()
  const history = scope.run(() => useVersionHistory(id))!
  stop = () => scope.stop()
  return { id, history }
}

beforeEach(() => {
  vi.unstubAllGlobals()
})
afterEach(() => {
  stop()
  vi.unstubAllGlobals()
})

describe('useVersionHistory', () => {
  it('reads the first page of versions, newest first as the server gives them, and where the history stands', async () => {
    const { calls } = controlledFetch()
    const { history } = start()

    const loading = history.load()
    expect(history.list.loading).toBe(true)
    expect(calls[0]!.url).toBe(`/api/pages/${pageA}/versions?limit=50`)
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
    const { history } = start()
    const first = history.load()
    calls[0]!.resolve(jsonResponse(200, listOf([version('v3'), version('v2', 1)], 'c/1')))
    await first

    const more = history.loadMore()
    // A second press while the first is on its way asks nothing.
    void history.loadMore()
    expect(calls).toHaveLength(2)
    expect(calls[1]!.url).toBe(`/api/pages/${pageA}/versions?limit=50&before=c%2F1`)
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
    const { history } = start()
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
    const { history } = start()

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

  it('drops an answer for the page it was asked about once another page is asked about', async () => {
    const { calls } = controlledFetch()
    const { id, history } = start()
    void history.load()

    id.value = pageB
    await nextTick()
    // The new page is asked about at once; the old answer arrives last and changes nothing.
    expect(calls[1]!.url).toBe(`/api/pages/${pageB}/versions?limit=50`)
    calls[1]!.resolve(jsonResponse(200, listOf([version('b1')])))
    await flush()
    calls[0]!.resolve(jsonResponse(200, listOf([version('a1'), version('a0')], 'old')))
    await flush()

    expect(history.list.versions.map((v) => v.id)).toEqual(['b1'])
    expect(history.list.next).toBeNull()
  })

  it('reads a version once and keeps it, also when it is opened again', async () => {
    const { calls } = controlledFetch()
    const { history } = start()

    const opening = history.open('v1')
    expect(history.details.get('v1')).toEqual({ status: 'loading' })
    // Asked again while on its way: one request.
    void history.open('v1')
    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe(`/api/pages/${pageA}/versions/v1`)
    const detail = {
      id: 'v1',
      createdAt: version('v1').createdAt,
      kind: 'automatic',
      title: 'Then',
      content: { type: 'doc' },
    }
    calls[0]!.resolve(jsonResponse(200, detail))
    await opening

    expect(history.details.get('v1')).toEqual({ status: 'ready', detail })
    await history.open('v1')
    expect(calls).toHaveLength(1)
  })

  it('says why a version could not be read, and reads it again when asked', async () => {
    const { calls } = controlledFetch()
    const { history } = start()

    const failing = history.open('v1')
    calls[0]!.resolve(jsonResponse(503, { code: 'history_unavailable' }))
    await failing
    expect(history.details.get('v1')).toEqual({ status: 'error', code: 'history_unavailable' })

    const again = history.open('v1')
    expect(calls).toHaveLength(2)
    calls[1]!.resolve(
      jsonResponse(200, {
        id: 'v1',
        createdAt: '',
        kind: 'automatic',
        title: '',
        content: { type: 'doc' },
      }),
    )
    await again
    expect(history.details.get('v1')?.status).toBe('ready')
  })

  it('forgets what it read about a page, and drops a version that arrives late, once another page is asked about', async () => {
    const { calls } = controlledFetch()
    const { id, history } = start()
    const opening = history.open('v1')

    id.value = pageB
    await nextTick()
    calls[0]!.resolve(
      jsonResponse(200, {
        id: 'v1',
        createdAt: '',
        kind: 'automatic',
        title: '',
        content: { type: 'doc' },
      }),
    )
    await opening

    expect(history.details.size).toBe(0)
  })
})
