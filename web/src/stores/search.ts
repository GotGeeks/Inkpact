import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, api } from '../api'
import type { SearchHit, SearchResponse } from '../search/types'
import { useTreeStore } from './tree'

/** Fewer letters than this match nearly every page: the dialog asks for more instead of searching. */
export const MIN_SEARCH_LENGTH = 2

/** How long after the last key a search starts. */
export const SEARCH_DEBOUNCE_MS = 150

export type SearchState = 'idle' | 'loading' | 'done' | 'error'
export type PaletteScope = 'pages' | 'tags' | 'commands'

/**
 * The search dialog's state: whether it is open, what was typed and what the server found. The text stays after the
 * dialog is closed, so that opening it again shows the last search. A new search cancels the one before it, and only the
 * answer to the newest text is ever shown. Errors are a state of the dialog, with the server's code, not thrown.
 */
export const useSearchStore = defineStore('search', () => {
  const tree = useTreeStore()

  const open = ref(false)
  const scope = ref<PaletteScope>('pages')
  const helpOpen = ref(false)
  const scopeBeforeHelp = ref<PaletteScope>('pages')
  const query = ref('')
  const hits = ref<SearchHit[]>([])
  const more = ref(false)
  const state = ref<SearchState>('idle')
  /** The text the hits on screen were found for. */
  const searchedFor = ref('')
  /** Why the last search failed: `network_error`, or a code from the server. */
  const errorCode = ref<string | null>(null)

  let timer: ReturnType<typeof setTimeout> | undefined
  let controller: AbortController | undefined
  let latest = 0

  function show(nextScope: PaletteScope = 'pages') {
    if (!tree.workspace) return
    if (open.value) {
      if (nextScope !== scope.value) setScope(nextScope)
      return
    }
    open.value = true
    helpOpen.value = false
    scope.value = nextScope
    if (nextScope === 'pages' && query.value.trim().length >= MIN_SEARCH_LENGTH) flush()
  }

  function setScope(nextScope: PaletteScope) {
    if (scope.value === nextScope || helpOpen.value) return
    if (scope.value === 'pages') {
      clearTimeout(timer)
      cancel()
      if (state.value === 'loading') state.value = 'idle'
    }
    scope.value = nextScope
    if (nextScope === 'pages' && open.value && query.value.trim().length >= MIN_SEARCH_LENGTH) {
      flush()
    }
  }

  function showHelp() {
    if (!open.value || helpOpen.value) return
    scopeBeforeHelp.value = scope.value
    helpOpen.value = true
    if (scope.value === 'pages') {
      clearTimeout(timer)
      cancel()
      if (state.value === 'loading') state.value = 'idle'
    }
  }

  function hideHelp() {
    if (!helpOpen.value) return
    helpOpen.value = false
    scope.value = scopeBeforeHelp.value
    if (scope.value === 'pages' && query.value.trim().length >= MIN_SEARCH_LENGTH) flush()
  }

  function hide() {
    open.value = false
    clearTimeout(timer)
    cancel()
    if (state.value === 'loading') state.value = 'idle'
    helpOpen.value = false
    scope.value = 'pages'
  }

  function setQuery(text: string) {
    query.value = text
    clearTimeout(timer)
    if (text.trim().length < MIN_SEARCH_LENGTH) {
      // Nothing to look for: what was on screen is not the answer to this text any more.
      cancel()
      clear()
      state.value = 'idle'
      return
    }

    timer = setTimeout(() => void run(), SEARCH_DEBOUNCE_MS)
  }

  /** Searches for the text now, without waiting for the pause after the last key. */
  function flush() {
    clearTimeout(timer)
    void run()
  }

  function clear() {
    hits.value = []
    more.value = false
    searchedFor.value = ''
    errorCode.value = null
  }

  function cancel() {
    latest++
    controller?.abort()
    controller = undefined
  }

  async function run() {
    const workspace = tree.workspace
    const text = query.value.trim()
    if (!open.value || scope.value !== 'pages' || !workspace || text.length < MIN_SEARCH_LENGTH)
      return

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
      errorCode.value = null
      state.value = 'done'
    } catch (error) {
      // Cancelled by a newer search, or by closing: the newer one has its own say.
      if (mine !== latest || (error instanceof DOMException && error.name === 'AbortError')) return
      // The old list is not the answer to this text: it goes, so it cannot be opened as if it were.
      clear()
      errorCode.value = error instanceof ApiError ? error.code : 'unknown'
      state.value = 'error'
    }
  }

  function reset() {
    cancel()
    clearTimeout(timer)
    open.value = false
    scope.value = 'pages'
    helpOpen.value = false
    scopeBeforeHelp.value = 'pages'
    query.value = ''
    clear()
    state.value = 'idle'
  }

  return {
    open,
    scope,
    helpOpen,
    query,
    hits,
    more,
    state,
    searchedFor,
    errorCode,
    show,
    setScope,
    showHelp,
    hideHelp,
    hide,
    setQuery,
    flush,
    reset,
  }
})
