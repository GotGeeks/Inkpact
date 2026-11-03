import { watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError, api } from '../api'
import { useNotificationsStore } from '../stores/notifications'
import { useSyncStore, type LostEdits } from '../stores/sync'
import { useTreeStore } from '../stores/tree'
import type { PageChange, PageDetail } from '../tree/types'
import { copyUpdate, newPageId, toBase64 } from './pageCopy'

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
 * made on, in that page's workspace, titled after it, and tells the person where they are.
 *
 * The page and its content are made by one request, with an id chosen here: when its answer is lost and it is sent again,
 * the server gives the page it made rather than a second one, and there is never a page without its content. When the
 * place beside the page is gone (it or its parent is in the trash), the copy goes to the top level. A copy that cannot be
 * made now is tried again when the connection is back or the workspace has been read; the person is told once that it is
 * waiting. Until then the edits wait in the tab, and they go when the person signs out.
 */
export function useKeepLostEdits() {
  const { t } = useI18n()
  const sync = useSyncStore()
  const tree = useTreeStore()
  const notifications = useNotificationsStore()
  const busy = new Set<number>()
  /** Lost edits the person was told could not be kept yet, so a failure that comes back is not said again. */
  const told = new Set<number>()
  /** The id each copy gets: every try for the same edits asks for the same page. */
  const copyIds = new Map<number, string>()

  const stillWaiting = (lost: LostEdits) => sync.lostEdits.some((other) => other.id === lost.id)

  /** The page the edits were made on, as the server has it; null when it is gone altogether. */
  async function pageOf(pageId: string): Promise<PageDetail | null> {
    try {
      return await api<PageDetail>('GET', `/api/pages/${encodeURIComponent(pageId)}`)
    } catch (error) {
      if (error instanceof ApiError && error.code === 'not_found') return null
      throw error
    }
  }

  async function keep(lost: LostEdits) {
    if (busy.has(lost.id)) return
    busy.add(lost.id)
    let title = t('tree.untitled')
    try {
      const original = await pageOf(lost.pageId)
      // A page that is gone has no workspace to ask about: its copy goes to the one on screen, the person's own.
      const workspaceId = original?.workspaceId ?? tree.workspace?.id
      if (!workspaceId) return
      title = original?.page.title || title

      // As much of the original's title as fits beside the words around it.
      const room = MAX_TITLE_LENGTH - t('sync.offlineCopyTitle', { title: '' }).length
      const id = copyIds.get(lost.id) ?? newPageId()
      copyIds.set(lost.id, id)
      const body = {
        id,
        title: t('sync.offlineCopyTitle', { title: cut(title, room) }),
        content: toBase64(copyUpdate(lost.before)),
        beforeId: null,
      }
      const create = (parentId: string | null, afterId: string | null) =>
        api<PageChange>('POST', `/api/workspaces/${encodeURIComponent(workspaceId)}/pages`, {
          ...body,
          parentId,
          afterId,
        })

      let made: PageChange
      const beside = original && !original.inTrash ? original.page : null
      try {
        made = await create(beside?.parentId ?? null, beside?.id ?? null)
      } catch (error) {
        if (!beside || unreachable(error)) throw error
        made = await create(null, null)
      }

      // The page is made with the edits in it. A person who left meanwhile has their copy, and nobody else is told.
      if (!stillWaiting(lost)) return
      sync.forgetLostEdits(lost.id)
      copyIds.delete(lost.id)
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
