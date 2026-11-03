import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '../api'
import type { SearchHit, SearchResponse } from '../search/types'
import { useTreeStore } from './tree'

/** Fewer letters than this match nearly every page: the dialog asks for more instead of searching. */
export const MIN_SEARCH_LENGTH = 2

/** How long after the last key a search starts. */
export const SEARCH_DEBOUNCE_MS = 150

export type SearchState = 'idle' | 'loading' | 'done' | 'error'

/**
 * The search dialog's state: whether it is open, what was typed and what the server found. The text stays after the
 * dialog is closed, so that opening it again shows the last search. A new search cancels the one before it, and only the
 * answer to the newest text is ever shown. Errors are shown as a state of the dialog, not thrown.
 */
export const useSearchStore = defineStore('search', () => {
  const tree = useTreeStore()

  const open = ref(false)
  const query = ref('')
  const hits = ref<SearchHit[]>([])
  const more = ref(false)
  const state = ref<SearchState>('idle')
  /** The text the hits on screen were found for. */
  const searchedFor = ref('')

  let timer: ReturnType<typeof setTimeout> | undefined
  let controller: AbortController | undefined
  let latest = 0

  function show() {
    if (!tree.workspace) return
    open.value = true
    // The last search, found again: pages change.
    if (query.value.trim().length >= MIN_SEARCH_LENGTH) void run()
  }

  function hide() {
    open.value = false
    clearTimeout(timer)
  }

  function setQuery(text: string) {
    query.value = text
    clearTimeout(timer)
    if (text.trim().length < MIN_SEARCH_LENGTH) {
      // Nothing to look for: what was on screen is not the answer to this text any more.
      cancel()
      hits.value = []
      more.value = false
      searchedFor.value = ''
      state.value = 'idle'
      return
    }

    timer = setTimeout(() => void run(), SEARCH_DEBOUNCE_MS)
  }

  function cancel() {
    latest++
    controller?.abort()
    controller = undefined
  }

  async function run() {
    const workspace = tree.workspace
    const text = query.value.trim()
    if (!workspace || text.length < MIN_SEARCH_LENGTH) return

    cancel()
    const mine = latest
    controller = new AbortController()
    state.value = 'loading'
    try {
      const answer = await api<SearchResponse>(
        'GET',
        `/api/workspaces/${workspace.id}/search?q=${encodeURIComponent(text)}`,
        undefined,
        controller.signal,
      )
      if (mine !== latest) return
      hits.value = answer.results
      more.value = answer.more
      searchedFor.value = text
      state.value = 'done'
    } catch (error) {
      // Cancelled by a newer search, or by closing: the newer one has its own say.
      if (mine !== latest || (error instanceof DOMException && error.name === 'AbortError')) return
      state.value = 'error'
    }
  }

  function reset() {
    cancel()
    clearTimeout(timer)
    open.value = false
    query.value = ''
    hits.value = []
    more.value = false
    searchedFor.value = ''
    state.value = 'idle'
  }

  return { open, query, hits, more, state, searchedFor, show, hide, setQuery, run, reset }
})
