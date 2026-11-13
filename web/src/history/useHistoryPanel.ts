import { ref, shallowRef, watch, type Ref } from 'vue'
import { useNotificationsStore } from '../stores/notifications'

export type HistoryPanelComponent = typeof import('./HistoryPanel.vue').default

/** The panel's code: a chunk of its own, with the preview's editor, fetched the first time the panel is asked for. */
const loadPanel = () => import('./HistoryPanel.vue').then((module) => module.default)

/**
 * Whether a page's history panel is open, and the panel's code (run B.5b). The code is fetched when the panel is first
 * asked for, and the panel opens only once it is there: a chunk that cannot be fetched (the network dropped, or a new
 * version of the app was installed and the old file is gone) is said in a notice, and the button works again. Another
 * page closes the panel; so does `hide`, which the page view also calls when the panel goes away with the page.
 */
export function useHistoryPanel(
  pageId: Ref<string>,
  load: () => Promise<HistoryPanelComponent> = loadPanel,
) {
  const notifications = useNotificationsStore()
  const component = shallowRef<HistoryPanelComponent | null>(null)
  const open = ref(false)
  /** The code is on its way. */
  const loading = ref(false)

  async function show() {
    if (open.value || loading.value) return
    const forPage = pageId.value
    if (!component.value) {
      loading.value = true
      try {
        component.value = await load()
      } catch (error) {
        console.error('The history panel could not be loaded', error)
        notifications.post({ type: 'error', code: 'history_open_failed' })
        return
      } finally {
        loading.value = false
      }
    }
    // Asked for on a page the person has left meanwhile: not opened over the next one.
    if (pageId.value === forPage) open.value = true
  }

  function hide() {
    open.value = false
  }

  watch(pageId, hide)

  return { component, open, loading, show, hide }
}
