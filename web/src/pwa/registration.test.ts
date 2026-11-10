import { beforeEach, describe, expect, it, vi } from 'vitest'

beforeEach(() => {
  vi.resetModules()
})

describe('service worker registration', () => {
  it('reports an already waiting update without activating or reloading it', async () => {
    const { registerPwa, pwaState } = await import('./registration')
    const worker = new EventTarget() as ServiceWorker
    const registration = {
      waiting: worker,
      installing: null,
      addEventListener: vi.fn(),
    }
    const container = { controller: worker, register: vi.fn().mockResolvedValue(registration) }
    await registerPwa(container)
    expect(container.register).toHaveBeenCalledWith('/service-worker.js')
    expect(pwaState.updateReady).toBe(true)
  })

  it('does not report the first installation as an update', async () => {
    const { registerPwa, pwaState } = await import('./registration')
    const worker = new EventTarget() as ServiceWorker
    const registration = {
      waiting: worker,
      installing: null,
      addEventListener: vi.fn(),
    }
    await registerPwa({ controller: null, register: vi.fn().mockResolvedValue(registration) })
    expect(pwaState.updateReady).toBe(false)
  })

  it('notices a worker that was already installing when registration returned', async () => {
    const { registerPwa, pwaState } = await import('./registration')
    const worker = new EventTarget() as ServiceWorker
    Object.defineProperty(worker, 'state', { value: 'installing', writable: true })
    const registration = {
      waiting: null,
      installing: worker,
      addEventListener: vi.fn(),
    }
    await registerPwa({ controller: worker, register: vi.fn().mockResolvedValue(registration) })
    Object.defineProperty(worker, 'state', { value: 'installed' })
    worker.dispatchEvent(new Event('statechange'))
    expect(pwaState.updateReady).toBe(true)
  })

  it('reports registration failures instead of claiming offline availability', async () => {
    const { registerPwa, pwaState } = await import('./registration')
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {})
    await registerPwa({
      controller: null,
      register: vi.fn().mockRejectedValue(new Error('blocked')),
    })
    expect(pwaState.registrationFailed).toBe(true)
    expect(logged).toHaveBeenCalledOnce()
    logged.mockRestore()
  })
})
