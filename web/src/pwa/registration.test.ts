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
    const container = Object.assign(new EventTarget(), {
      controller: worker,
      register: vi.fn().mockResolvedValue(registration),
    })
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
    await registerPwa(
      Object.assign(new EventTarget(), {
        controller: null,
        register: vi.fn().mockResolvedValue(registration),
      }),
    )
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
    await registerPwa(
      Object.assign(new EventTarget(), {
        controller: worker,
        register: vi.fn().mockResolvedValue(registration),
      }),
    )
    Object.defineProperty(worker, 'state', { value: 'installed' })
    worker.dispatchEvent(new Event('statechange'))
    expect(pwaState.updateReady).toBe(true)
  })

  it('reports an installing worker that becomes redundant', async () => {
    const { registerPwa, pwaState } = await import('./registration')
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {})
    const worker = new EventTarget() as ServiceWorker
    Object.defineProperty(worker, 'state', { value: 'installing', writable: true })
    const registration = {
      waiting: null,
      installing: worker,
      addEventListener: vi.fn(),
    }

    await registerPwa(
      Object.assign(new EventTarget(), {
        controller: null,
        register: vi.fn().mockResolvedValue(registration),
      }),
    )
    Object.defineProperty(worker, 'state', { value: 'redundant' })
    worker.dispatchEvent(new Event('statechange'))

    expect(pwaState.registrationFailed).toBe(true)
    expect(logged).toHaveBeenCalledOnce()
  })

  it('reports registration failures instead of claiming offline availability', async () => {
    const { registerPwa, pwaState } = await import('./registration')
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {})
    await registerPwa(
      Object.assign(new EventTarget(), {
        controller: null,
        register: vi.fn().mockRejectedValue(new Error('blocked')),
      }),
    )
    expect(pwaState.registrationFailed).toBe(true)
    expect(logged).toHaveBeenCalledOnce()
    logged.mockRestore()
  })

  it('updates only when asked, and reloads once the new worker controls the tab', async () => {
    const { registerPwa, applyUpdate } = await import('./registration')
    const waiting = Object.assign(new EventTarget(), {
      postMessage: vi.fn(),
    }) as unknown as ServiceWorker
    const registration = { waiting, installing: null, addEventListener: vi.fn() }
    const container = Object.assign(new EventTarget(), {
      controller: new EventTarget() as ServiceWorker,
      register: vi.fn().mockResolvedValue(registration),
    })
    const reload = vi.fn()
    await registerPwa(container, reload)

    // A takeover nobody asked for (another tab's update) does not reload this one.
    container.dispatchEvent(new Event('controllerchange'))
    expect(reload).not.toHaveBeenCalled()
    expect(waiting.postMessage).not.toHaveBeenCalled()

    expect(applyUpdate()).toBe(true)
    expect(waiting.postMessage).toHaveBeenCalledWith({ type: 'skip-waiting' })
    container.dispatchEvent(new Event('controllerchange'))
    expect(reload).toHaveBeenCalledOnce()
  })

  it('says so when the worker refuses because another tab still runs the old version', async () => {
    const { registerPwa, applyUpdate, pwaState } = await import('./registration')
    const waiting = Object.assign(new EventTarget(), {
      postMessage: vi.fn(),
    }) as unknown as ServiceWorker
    const container = Object.assign(new EventTarget(), {
      controller: new EventTarget() as ServiceWorker,
      register: vi.fn().mockResolvedValue({ waiting, installing: null, addEventListener: vi.fn() }),
    })
    const reload = vi.fn()
    await registerPwa(container, reload)

    applyUpdate()
    container.dispatchEvent(new MessageEvent('message', { data: { type: 'close-other-tabs' } }))
    expect(pwaState.otherTabsOpen).toBe(true)
    container.dispatchEvent(new Event('controllerchange'))
    expect(reload).not.toHaveBeenCalled()
  })

  it('has nothing to apply without a waiting worker', async () => {
    const { registerPwa, applyUpdate } = await import('./registration')
    await registerPwa(
      Object.assign(new EventTarget(), {
        controller: null,
        register: vi
          .fn()
          .mockResolvedValue({ waiting: null, installing: null, addEventListener: vi.fn() }),
      }),
    )
    expect(applyUpdate()).toBe(false)
  })
})
