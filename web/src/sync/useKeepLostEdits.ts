import { watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError, api } from '../api'
import { useNotificationsStore } from '../stores/notifications'
import { useSyncStore, type LostEdits } from '../stores/sync'
import { useTreeStore } from '../stores/tree'
import type { PageChange } from '../tree/types'

/** The longest title the server takes, in UTF-16 units, as it counts them (TreeService.MaxTitleLength). */
const MAX_TITLE_LENGTH = 500

/** The start of `text` that is at most `max` UTF-16 units long, never cutting a character in half. */
function cut(text: string, max: number): string {
  let result = ''
  for (const character of text) {
    if (result.length + character.length > max) break
    result += character
  }
  return result
}

const unreachable = (error: unknown) => error instanceof ApiError && error.code === 'network_error'

/**
 * Keeps edits that a Sync deleted (see `onEditsLost` in the sync client) as a new page right after the page they were
 * made on, titled after it, and tells the person where they are. When the place beside the page is gone (its parent was
 * put in the trash meanwhile), the copy goes to the top level. A copy that cannot be made now is tried again when the
 * connection is back or the workspace has loaded; the person is told once that it is waiting. Until then the edits wait
 * in the tab, and they go when the person signs out: they are never written for whoever signs in next.
 */
export function useKeepLostEdits() {
  const { t } = useI18n()
  const sync = useSyncStore()
  const tree = useTreeStore()
  const notifications = useNotificationsStore()
  const busy = new Set<number>()
  /** Lost edits the person was told could not be kept yet, so a failure that comes back is not said again. */
  const told = new Set<number>()

  const stillWaiting = (lost: LostEdits) => sync.lostEdits.some((other) => other.id === lost.id)

  function create(
    workspaceId: string,
    title: string,
    parentId: string | null,
    afterId: string | null,
  ) {
    return api<PageChange>('POST', `/api/workspaces/${workspaceId}/pages`, {
      title,
      parentId,
      afterId,
      beforeId: null,
    })
  }

  async function keep(lost: LostEdits) {
    const workspaceId = tree.workspace?.id
    if (busy.has(lost.id) || !workspaceId) return
    busy.add(lost.id)
    const original = tree.byId.get(lost.pageId)
    const title = original?.title || t('tree.untitled')
    try {
      // As much of the original's title as fits beside the words around it.
      const room = MAX_TITLE_LENGTH - t('sync.offlineCopyTitle', { title: '' }).length
      const copyTitle = t('sync.offlineCopyTitle', { title: cut(title, room) })
      let made: PageChange
      try {
        // Beside the original when it is in the tree; at the top level when it is not (in the trash, or not loaded).
        made = await create(
          workspaceId,
          copyTitle,
          original?.parentId ?? null,
          original?.id ?? null,
        )
      } catch (error) {
        if (!original || unreachable(error)) throw error
        made = await create(workspaceId, copyTitle, null, null)
      }

      // The person left while the page was made: their edits are not written, not for them and not for anyone after them.
      if (!stillWaiting(lost) || !sync.writeCopy(made.page.id, lost.before)) return
      sync.forgetLostEdits(lost.id)
      told.delete(lost.id)
      notifications.post({ type: 'editsKept', pageId: made.page.id, title })
      await tree.load().catch(() => undefined)
    } catch {
      if (stillWaiting(lost) && !told.has(lost.id)) {
        told.add(lost.id)
        notifications.post({ type: 'editsWaiting', title })
      }
    } finally {
      busy.delete(lost.id)
    }
  }

  watch(
    [() => sync.lostEdits, () => sync.display, () => tree.workspace],
    ([all, display]) => {
      if (display !== 'live') return
      for (const lost of all) void keep(lost)
    },
    { immediate: true },
  )
}
