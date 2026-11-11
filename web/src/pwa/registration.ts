import { reactive, readonly } from 'vue'

interface WorkerRegistration {
  readonly waiting: ServiceWorker | null
  readonly installing: ServiceWorker | null
  addEventListener(type: 'updatefound', listener: () => void): void
}

interface WorkerContainer {
  readonly controller: ServiceWorker | null
  register(url: string): Promise<WorkerRegistration>
  addEventListener(type: 'controllerchange' | 'message', listener: (event: Event) => void): void
}

const state = reactive({
  updateReady: false,
  registrationFailed: false,
  /** "Update now" was refused: another Inkpact tab still runs the old version. */
  otherTabsOpen: false,
})
export const pwaState = readonly(state)

let waitingWorker: () => ServiceWorker | null = () => null
let updating = false

/**
 * Asks the waiting worker to take over this tab, which then reloads with the new version. The caller offers it only
 * when this tab has nothing unsaved; the worker refuses while another tab is open, as that tab still needs the old
 * version's files. Never called by itself: an update is the person's choice.
 */
export function applyUpdate(): boolean {
  const waiting = waitingWorker()
  if (!waiting) return false
  state.otherTabsOpen = false
  updating = true
  waiting.postMessage({ type: 'skip-waiting' })
  return true
}

/** Updates wait for all old clients to leave, or for "Update now" in the only tab; no tab is taken over unasked. */
export async function registerPwa(
  container: WorkerContainer,
  reload: () => void = () => window.location.reload(),
) {
  container.addEventListener('controllerchange', () => {
    if (updating) reload()
  })
  container.addEventListener('message', (event) => {
    if ((event as MessageEvent).data?.type !== 'close-other-tabs') return
    updating = false
    state.otherTabsOpen = true
  })
  try {
    const registration = await container.register('/service-worker.js')
    waitingWorker = () => registration.waiting
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
