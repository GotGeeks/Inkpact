import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { ApiError, api } from '../api'
import { onTabVisible } from '../composables/onTabVisible'
import { useNotificationsStore } from './notifications'
import { useTreeStore } from './tree'

/**
 * The pages the person starred (run B.3), in the order they were starred. Kept on the server, so the phone and the laptop
 * share them: read when the app starts and again when the tab gets back in view, since a star from the other device
 * should be here when the person returns. A star or an unstar shows at once and is put back, with a notice, when the
 * server refuses.
 */
export const useFavoritesStore = defineStore('favorites', () => {
  const ids = ref<string[]>([])
  const notifications = useNotificationsStore()
  // The newest read wins, and a change made while one is under way is not undone by its older answer.
  let reads = 0
  let changes = 0
  let stopWatching: (() => void) | null = null

  // The sidebar asks for every row it shows: one lookup each, not a walk through the list.
  const starred = computed(() => new Set(ids.value))
  const has = (id: string) => starred.value.has(id)

  async function load() {
    const workspace = useTreeStore().workspace
    if (!workspace) return
    const mine = ++reads
    const startedAt = changes
    try {
      const answer = await api<string[]>('GET', `/api/workspaces/${workspace.id}/favorites`)
      if (mine === reads && startedAt === changes) ids.value = answer
    } catch {
      // The list is a convenience: without it the sidebar simply has no favorites.
    }
  }

  async function toggle(id: string) {
    const starring = !has(id)
    const position = ids.value.indexOf(id)
    changes++
    ids.value = starring ? [...ids.value, id] : ids.value.filter((other) => other !== id)
    try {
      await api(starring ? 'PUT' : 'DELETE', `/api/pages/${encodeURIComponent(id)}/favorite`)
    } catch (error) {
      // Only this change is undone: another toggle that went through meanwhile stays.
      changes++
      if (starring) ids.value = ids.value.filter((other) => other !== id)
      else if (!has(id)) {
        const next = [...ids.value]
        next.splice(Math.min(position, next.length), 0, id)
        ids.value = next
      }
      notifications.post({
        type: 'error',
        code: error instanceof ApiError ? error.code : 'unknown',
      })
    }
  }

  /** Reads the list again whenever the tab comes back into view. */
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
    changes++
    ids.value = []
  }

  return { ids, has, load, toggle, watchFocus, unwatchFocus, reset }
})
