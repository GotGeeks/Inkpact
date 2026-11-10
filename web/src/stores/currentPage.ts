import { defineStore } from 'pinia'
import { ref } from 'vue'

/**
 * The page the page view shows, as it fetched it: also a page in the trash, which the live tree does not hold. The tab's
 * title and the shell's announcement read it, so they name what is on the screen.
 */
export const useCurrentPageStore = defineStore('currentPage', () => {
  /** The page's id once the view knows what it is (found or not), else null. */
  const id = ref<string | null>(null)
  const title = ref('')
  const missing = ref(false)
  const inTrash = ref(false)
  const focusTitleFor = ref<string | null>(null)

  function show(pageId: string, pageTitle: string, isMissing: boolean, isInTrash = false) {
    id.value = pageId
    title.value = pageTitle
    missing.value = isMissing
    inTrash.value = isInTrash
  }

  function requestTitleFocus(pageId: string) {
    focusTitleFor.value = pageId
  }

  function consumeTitleFocus(pageId: string): boolean {
    if (focusTitleFor.value !== pageId) return false
    focusTitleFor.value = null
    return true
  }

  function cancelTitleFocus(pageId?: string) {
    if (pageId === undefined || focusTitleFor.value === pageId) focusTitleFor.value = null
  }

  function clear() {
    id.value = null
    title.value = ''
    missing.value = false
    inTrash.value = false
    focusTitleFor.value = null
  }

  return {
    id,
    title,
    missing,
    inTrash,
    show,
    requestTitleFocus,
    consumeTitleFocus,
    cancelTitleFocus,
    clear,
  }
})
