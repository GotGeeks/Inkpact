import { reactive, shallowReactive } from 'vue'
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
 * One page's history while the panel is open (run B.5b): its versions, newest first and read in pages, where its history
 * stands, and the content of the versions that were looked at. The panel is made for one page and goes when another page
 * opens, so this never changes page.
 *
 * Content is kept once read, for as long as the panel is open: the server's answers are never cached by the browser, and
 * each read is a replay in the sidecar, which people who are editing wait behind. Going back to a version shown before
 * asks nothing.
 */
export function useVersionHistory(pageId: string) {
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

  // Raised by every read of the first page: older versions that arrive after it would add to a list read again, and are
  // dropped. Only the list's: a version's content is the same whichever list asked for it.
  let listGeneration = 0

  const path = `/api/pages/${encodeURIComponent(pageId)}/versions`

  function codeOf(error: unknown): string {
    return error instanceof ApiError ? error.code : 'unknown'
  }

  async function load() {
    const mine = ++listGeneration
    list.loading = true
    list.error = null
    list.moreError = null
    list.loadingMore = false
    try {
      const answer = await api<VersionList>('GET', `${path}?limit=${PAGE_SIZE}`)
      if (mine !== listGeneration) return
      list.versions = answer.versions
      list.next = answer.next
      list.state = answer.state
      list.failure = answer.failure
      list.loaded = true
    } catch (error) {
      if (mine !== listGeneration) return
      list.error = codeOf(error)
    } finally {
      if (mine === listGeneration) list.loading = false
    }
  }

  async function loadMore() {
    const cursor = list.next
    if (!cursor || list.loadingMore || list.loading) return
    const mine = listGeneration
    list.loadingMore = true
    list.moreError = null
    try {
      const answer = await api<VersionList>(
        'GET',
        `${path}?limit=${PAGE_SIZE}&before=${encodeURIComponent(cursor)}`,
      )
      if (mine !== listGeneration) return
      // A version that is in both pages (made while the person read) is shown once.
      const seen = new Set(list.versions.map((version) => version.id))
      list.versions = [...list.versions, ...answer.versions.filter((v) => !seen.has(v.id))]
      list.next = answer.next
      list.state = answer.state
      list.failure = answer.failure
    } catch (error) {
      if (mine !== listGeneration) return
      list.moreError = codeOf(error)
    } finally {
      if (mine === listGeneration) list.loadingMore = false
    }
  }

  /** Reads a version's content, unless it is there already or on its way; one that failed is read again. */
  async function open(versionId: string) {
    const known = details.get(versionId)
    if (known && known.status !== 'error') return
    details.set(versionId, { status: 'loading' })
    try {
      const detail = await api<VersionDetail>('GET', `${path}/${encodeURIComponent(versionId)}`)
      details.set(versionId, { status: 'ready', detail })
    } catch (error) {
      details.set(versionId, { status: 'error', code: codeOf(error) })
    }
  }

  return { list, details, load, loadMore, open }
}
