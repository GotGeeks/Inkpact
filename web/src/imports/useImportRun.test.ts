import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, nextTick, ref } from 'vue'
import type { ImportRun } from './importApi'
import { useImportRun } from './useImportRun'
import { jsonResponse } from '../testing/helpers'

function run(state: ImportRun['state'], processed = 0): ImportRun {
  return {
    id: 'i1',
    workspaceId: 'w1',
    fileName: 'export.jex',
    state,
    errorCode: null,
    totalItems: 10,
    processedItems: processed,
    createdAt: '2026-10-28T10:00:00Z',
    startedAt: null,
    finishedAt: null,
    counts: {} as ImportRun['counts'],
    report: state === 'completed' ? [] : null,
  }
}

beforeEach(() => vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] }))
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

/** Answers each read in turn: a run, an error status, or a failure to connect. */
function answers(...next: (ImportRun | number | 'offline')[]) {
  const fetchMock = vi.fn(async () => {
    const answer = next.length > 1 ? next.shift()! : next[0]!
    if (answer === 'offline') throw new TypeError('Failed to fetch')
    if (typeof answer === 'number') return jsonResponse(answer, { code: 'not_found' })
    return jsonResponse(200, answer)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function follow(id: string | null, onEnded?: (run: ImportRun) => void) {
  const current = ref(id)
  let result!: ReturnType<typeof useImportRun>
  const wrapper = mount(
    defineComponent({
      setup() {
        result = useImportRun(current, { intervalMs: 1000, onEnded })
        return () => null
      },
    }),
  )
  return { current, wrapper, ...result }
}

describe('useImportRun', () => {
  it('reads the import now and again every second, until it has ended', async () => {
    const fetchMock = answers(run('queued'), run('running', 4), run('completed', 10))
    const onEnded = vi.fn()

    const { run: followed } = follow('i1', onEnded)
    await flushPromises()
    expect(followed.value?.state).toBe('queued')

    await vi.advanceTimersByTimeAsync(1000)
    expect(followed.value?.processedItems).toBe(4)
    expect(onEnded).not.toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(1000)
    expect(followed.value?.state).toBe('completed')
    expect(onEnded).toHaveBeenCalledOnce()

    await vi.advanceTimersByTimeAsync(5000)
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('does not read anything without an id', async () => {
    const fetchMock = answers(run('queued'))

    const { run: followed } = follow(null)
    await vi.advanceTimersByTimeAsync(3000)

    expect(fetchMock).not.toHaveBeenCalled()
    expect(followed.value).toBeNull()
  })

  it('stops with the error code when the import is not there, or is not the person’s', async () => {
    const fetchMock = answers(404)

    const { error, run: followed } = follow('i1')
    await flushPromises()
    await vi.advanceTimersByTimeAsync(5000)

    expect(error.value).toBe('not_found')
    expect(followed.value).toBeNull()
    expect(fetchMock).toHaveBeenCalledOnce()
  })

  it('keeps asking when the server cannot be reached, and says so after a few tries', async () => {
    answers('offline', 'offline', 'offline', run('running', 2))

    const { connectionLost, run: followed } = follow('i1')
    await flushPromises()
    expect(connectionLost.value).toBe(false)

    await vi.advanceTimersByTimeAsync(2000)
    expect(connectionLost.value).toBe(true)
    expect(followed.value).toBeNull()

    await vi.advanceTimersByTimeAsync(1000)
    expect(connectionLost.value).toBe(false)
    expect(followed.value?.processedItems).toBe(2)
  })

  it('keeps asking after a server error or a rate limit', async () => {
    answers(503, 429, run('running', 1))

    const { error, run: followed } = follow('i1')
    await flushPromises()
    await vi.advanceTimersByTimeAsync(2000)

    expect(error.value).toBeNull()
    expect(followed.value?.state).toBe('running')
  })

  it('waits as long as a rate limit says, without calling it a lost connection', async () => {
    const limited = () =>
      new Response(JSON.stringify({ code: 'too_many_requests' }), {
        status: 429,
        headers: { 'Content-Type': 'application/json', 'Retry-After': '30' },
      })
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(limited())
      .mockResolvedValueOnce(limited())
      .mockResolvedValueOnce(limited())
      .mockResolvedValue(jsonResponse(200, run('running', 3)))
    vi.stubGlobal('fetch', fetchMock)

    const { connectionLost, run: followed } = follow('i1')
    await flushPromises()
    await vi.advanceTimersByTimeAsync(29_000)
    expect(fetchMock).toHaveBeenCalledOnce()

    await vi.advanceTimersByTimeAsync(61_000)
    expect(fetchMock).toHaveBeenCalledTimes(4)
    expect(connectionLost.value).toBe(false)
    expect(followed.value?.processedItems).toBe(3)
  })

  it('stops when the page closes', async () => {
    const fetchMock = answers(run('running'))

    const { wrapper } = follow('i1')
    await flushPromises()
    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(5000)

    expect(fetchMock).toHaveBeenCalledOnce()
  })

  it('starts over for another import, and ignores a late answer for the old one', async () => {
    let release!: (response: Response) => void
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(() => new Promise<Response>((resolve) => (release = resolve)))
      .mockResolvedValue(jsonResponse(200, { ...run('running', 7), id: 'i2' }))
    vi.stubGlobal('fetch', fetchMock)

    const { current, run: followed } = follow('i1')
    current.value = 'i2'
    await nextTick()
    await flushPromises()
    expect(followed.value?.id).toBe('i2')

    release(jsonResponse(200, { ...run('completed', 10), id: 'i1' }))
    await flushPromises()

    expect(followed.value?.id).toBe('i2')
    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual([
      '/api/imports/i1',
      '/api/imports/i2',
    ])
  })
})
