import { describe, expect, it, vi } from 'vitest'
import { openOfflineDatabase } from './indexedDb'

describe('offline IndexedDB opening', () => {
  it('surfaces a blocked upgrade and closes a late successful connection', async () => {
    const close = vi.fn()
    const request = {
      onupgradeneeded: null,
      onblocked: null,
      onerror: null,
      onsuccess: null,
      result: { close },
    } as unknown as IDBOpenDBRequest
    const factory = { open: () => request } as unknown as IDBFactory
    const opened = openOfflineDatabase(factory)
    request.onblocked?.call(request, new Event('blocked') as IDBVersionChangeEvent)
    await expect(opened).rejects.toThrow('blocked')
    request.onsuccess?.call(request, new Event('success'))
    expect(close).toHaveBeenCalledOnce()
  })

  it('surfaces an unavailable database', async () => {
    const failure = new DOMException('Unavailable', 'InvalidStateError')
    const factory = {
      open: () => {
        throw failure
      },
    } as unknown as IDBFactory
    await expect(openOfflineDatabase(factory)).rejects.toBe(failure)
  })
})
