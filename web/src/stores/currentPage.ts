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

  function show(pageId: string, pageTitle: string, isMissing: boolean) {
    id.value = pageId
    title.value = pageTitle
    missing.value = isMissing
  }

  function clear() {
    id.value = null
    title.value = ''
    missing.value = false
  }

  return { id, title, missing, show, clear }
})
