import { defineStore } from 'pinia'
import { ref } from 'vue'

/**
 * Things the person should be told about, shown as toasts. They carry what happened, not the words: the toaster turns
 * them into text in the language of the page.
 */
export type Notice =
  | { type: 'error'; code: string; retryAfterSeconds?: number }
  | { type: 'trashed'; pageId: string; title: string }
  | { type: 'restored'; pageId: string; title: string; toTopLevel: boolean }
  /** Offline edits to the page titled `title` were deleted by a change from elsewhere, and kept in the page `pageId`. */
  | { type: 'editsKept'; pageId: string; title: string }
  /** The copy of such edits could not be made yet: it is tried again when the connection is back. */
  | { type: 'editsWaiting'; title: string }
  /** The copy of such edits is larger than the server takes (a page too large to open), so it cannot be made. */
  | { type: 'editsTooLarge'; title: string }

export type PostedNotice = Notice & { id: number }

export const useNotificationsStore = defineStore('notifications', () => {
  const notices = ref<PostedNotice[]>([])
  let next = 1

  function post(notice: Notice) {
    notices.value.push({ ...notice, id: next++ })
  }

  function dismiss(id: number) {
    notices.value = notices.value.filter((notice) => notice.id !== id)
  }

  return { notices, post, dismiss }
})
