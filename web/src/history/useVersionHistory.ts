import { reactive, shallowReactive, watch, type Ref } from 'vue'
import { api, ApiError } from '../api'
import type { HistoryState, VersionDetail, VersionSummary, VersionList } from './types'

/** One version's content as the panel has it: on its way, there, or why it is not. */
export type DetailState =
  | { status: 'loading' }
  | { status: 'ready'; detail: VersionDetail }
  | { status: 'error'; code: string }

/** How many versions one request asks for: the server's own default. */
export const PAGE_SIZE = 50

/**
 * A page's history while the panel is open (run B.5b): its versions, newest first and read in pages, where its history
 * stands, and the content of the versions that were looked at.
 *
 * Content is kept once read, for as long as the panel is open: the server's answers are never cached by the browser, and
 * each read is a replay in the sidecar, which people who are editing wait behind. Going back to a version shown before
 * asks nothing.
 *
 * An answer that comes back for another page than the one now asked about (the panel moved on, or was opened for another
 * page meanwhile) is dropped.
 */
export function useVersionHistory(pageId: Ref<string>) {
  const list = reactive({
    versions: [] as VersionSummary[],
    next: null as string | null,
    state: 'ready' as HistoryState,
    failure: null as string | null,
    /** The first page is on its way. */
    loading: false,
    /** More versions are on their way. */
    loadingMore: false,
    /** The first page could not be read: its error code. */
    error: null as string | null,
    /** The next page could not be read: its error code. */
    moreError: null as string | null,
    /** The first page was read at least once. */
    loaded: false,
  })
  const details = shallowReactive(new Map<string, DetailState>())

  // Raised for every new page and every new first read: an answer of an older one finds it changed and is dropped.
  let generation = 0

  const path = (id: string) => `/api/pages/${encodeURIComponent(id)}/versions`

  function codeOf(error: unknown): string {
    return error instanceof ApiError ? error.code : 'unknown'
  }

  async function load() {
    const mine = ++generation
    const id = pageId.value
    list.loading = true
    list.error = null
    list.moreError = null
    try {
      const answer = await api<VersionList>('GET', `${path(id)}?limit=${PAGE_SIZE}`)
      if (mine !== generation) return
      list.versions = answer.versions
      list.next = answer.next
      list.state = answer.state
      list.failure = answer.failure
      list.loaded = true
    } catch (error) {
      if (mine !== generation) return
      list.error = codeOf(error)
    } finally {
      if (mine === generation) list.loading = false
    }
  }

  async function loadMore() {
    const cursor = list.next
    if (!cursor || list.loadingMore || list.loading) return
    const mine = generation
    const id = pageId.value
    list.loadingMore = true
    list.moreError = null
    try {
      const answer = await api<VersionList>(
        'GET',
        `${path(id)}?limit=${PAGE_SIZE}&before=${encodeURIComponent(cursor)}`,
      )
      if (mine !== generation) return
      // A version that is in both pages (made while the person read) is shown once.
      const seen = new Set(list.versions.map((version) => version.id))
      list.versions = [...list.versions, ...answer.versions.filter((v) => !seen.has(v.id))]
      list.next = answer.next
      list.state = answer.state
      list.failure = answer.failure
    } catch (error) {
      if (mine !== generation) return
      list.moreError = codeOf(error)
    } finally {
      if (mine === generation) list.loadingMore = false
    }
  }

  /** Reads a version's content, unless it is there already or on its way; one that failed is read again. */
  async function open(versionId: string) {
    const known = details.get(versionId)
    if (known && known.status !== 'error') return
    const mine = generation
    const id = pageId.value
    details.set(versionId, { status: 'loading' })
    try {
      const detail = await api<VersionDetail>('GET', `${path(id)}/${encodeURIComponent(versionId)}`)
      if (mine !== generation) return
      details.set(versionId, { status: 'ready', detail })
    } catch (error) {
      if (mine !== generation) return
      details.set(versionId, { status: 'error', code: codeOf(error) })
    }
  }

  function reset() {
    generation++
    list.versions = []
    list.next = null
    list.state = 'ready'
    list.failure = null
    list.loading = false
    list.loadingMore = false
    list.error = null
    list.moreError = null
    list.loaded = false
    details.clear()
  }

  watch(pageId, () => {
    reset()
    void load()
  })

  return { list, details, load, loadMore, open, reset }
}
