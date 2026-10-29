import { watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotificationsStore } from '../stores/notifications'
import { useSyncStore, type LostEdits } from '../stores/sync'
import { useTreeStore } from '../stores/tree'

/** How much of the original's title the copy's title keeps, so the two together stay under the server's limit (500). */
const MAX_TITLE_CHARACTERS = 400

/**
 * Keeps edits that a Sync deleted (see `onEditsLost` in the sync client) as a new page right after the page they were
 * made on, titled after it, and tells the person where they are. A copy that cannot be made now (the server is out of
 * reach again) is tried again when the connection is back; until then the edits wait in the tab.
 */
export function useKeepLostEdits() {
  const { t } = useI18n()
  const sync = useSyncStore()
  const tree = useTreeStore()
  const notifications = useNotificationsStore()
  const busy = new Set<number>()

  async function keep(lost: LostEdits) {
    if (busy.has(lost.id)) return
    busy.add(lost.id)
    try {
      const original = tree.byId.get(lost.pageId)
      const title = original?.title || t('tree.untitled')
      const short = Array.from(title).slice(0, MAX_TITLE_CHARACTERS).join('')
      // Beside the original when it is in the tree; at the top level when it is not (in the trash, or not loaded).
      const position = original
        ? { parentId: original.parentId, afterId: original.id }
        : { parentId: null }
      const made = await tree.create(t('sync.offlineCopyTitle', { title: short }), position)
      if (!made) return

      sync.writeCopy(made.page.id, lost.before)
      sync.forgetLostEdits(lost.id)
      notifications.post({ type: 'editsKept', pageId: made.page.id, title })
    } finally {
      busy.delete(lost.id)
    }
  }

  watch(
    [() => sync.lostEdits, () => sync.display],
    ([all, display]) => {
      if (display !== 'live') return
      for (const lost of all) void keep(lost)
    },
    { immediate: true },
  )
}
