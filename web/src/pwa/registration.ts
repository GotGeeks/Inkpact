import { reactive, readonly } from 'vue'

interface WorkerRegistration {
  readonly waiting: ServiceWorker | null
  readonly installing: ServiceWorker | null
  addEventListener(type: 'updatefound', listener: () => void): void
}

interface WorkerContainer {
  readonly controller: ServiceWorker | null
  register(url: string): Promise<WorkerRegistration>
}

const state = reactive({ updateReady: false, registrationFailed: false })
export const pwaState = readonly(state)

/** Updates wait for all old clients to leave; no tab is reloaded or taken over. */
export async function registerPwa(container: WorkerContainer) {
  try {
    const registration = await container.register('/service-worker.js')
    const noticeWaiting = () => {
      if (registration.waiting && container.controller) state.updateReady = true
    }
    const observed = new WeakSet<ServiceWorker>()
    const observeUpdate = () => {
      noticeWaiting()
      const installing = registration.installing
      if (!installing || observed.has(installing)) return
      observed.add(installing)
      if (installing.state === 'redundant') {
        console.error('The offline application shell worker became redundant during installation')
        state.registrationFailed = true
        return
      }
      if (installing.state === 'installed' && container.controller) state.updateReady = true
      installing.addEventListener('statechange', () => {
        if (installing.state === 'installed' && container.controller) state.updateReady = true
        if (installing.state === 'redundant') {
          console.error('The offline application shell worker became redundant during installation')
          state.registrationFailed = true
        }
      })
    }
    observeUpdate()
    registration.addEventListener('updatefound', observeUpdate)
  } catch (error) {
    console.error('The offline application shell could not be registered', error)
    state.registrationFailed = true
  }
}
