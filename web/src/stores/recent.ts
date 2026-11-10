import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, api } from '../api'
import { onTabVisible } from '../composables/onTabVisible'
import { useTreeStore } from './tree'

export interface RecentPage {
  pageId: string
  openedAt: string
}

/**
 * The pages the person opened last (run B.3), newest first, kept on the server so that a page opened on the phone is
 * listed on the laptop. Read when the start page opens and when its tab gets back in view. A page opened here is put at
 * the front at once, and a read waits for the visits this tab is still sending, so the list never misses the page just
 * left.
 */
export const useRecentStore = defineStore('recent', () => {
  const items = ref<RecentPage[]>([])
  const loaded = ref(false)
  const loadError = ref<string | null>(null)
  const sending = new Set<Promise<unknown>>()
  let reads = 0
  let stopWatching: (() => void) | null = null

  async function load(): Promise<boolean> {
    const workspace = useTreeStore().workspace
    if (!workspace) {
      loadError.value = 'unknown'
      return false
    }
    const mine = ++reads
    await Promise.allSettled([...sending])
    if (mine !== reads) return false
    try {
      const answer = await api<RecentPage[]>('GET', `/api/workspaces/${workspace.id}/recent`)
      if (mine === reads) {
        items.value = answer
        loaded.value = true
        loadError.value = null
      }
      return mine === reads
    } catch (error) {
      if (mine === reads) {
        loadError.value = error instanceof ApiError ? error.code : 'unknown'
      }
      return false
    }
  }

  /** Tells the server a page was opened. A failure is ignored: nobody needs to hear that a list could not be kept. */
  async function visit(pageId: string) {
    items.value = [
      { pageId, openedAt: new Date().toISOString() },
      ...items.value.filter((item) => item.pageId !== pageId),
    ]
    const request = api('POST', `/api/pages/${encodeURIComponent(pageId)}/visit`).catch(
      () => undefined,
    )
    sending.add(request)
    try {
      await request
    } finally {
      sending.delete(request)
    }
  }

  function watchFocus() {
    stopWatching ??= onTabVisible(() => void load())
  }

  function unwatchFocus() {
    stopWatching?.()
    stopWatching = null
  }

  function reset() {
    unwatchFocus()
    reads++
    items.value = []
    loaded.value = false
    loadError.value = null
  }

  return { items, loaded, load, loadError, visit, watchFocus, unwatchFocus, reset }
})
