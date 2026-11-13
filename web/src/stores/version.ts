import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, ApiError } from '../api'
import { useAuthStore } from './auth'

/** A release newer than the running app, as the server says for an administrator. */
export interface AvailableUpdate {
  version: string
  url: string
}

export interface VersionInfo {
  version: string
  /** A developer's build, without a version: the server's word, which also decides that it never asks for updates. */
  development: boolean
  update: AvailableUpdate | null
}

/**
 * Whether a link may be put in the page. Which pages count as release notes is the server's rule (UpdateCheck); the
 * client only refuses what no link should be (`javascript:` and the like), as Vue puts an href in as it is.
 */
export function isSafeLink(url: string): boolean {
  try {
    return new URL(url).protocol === 'https:'
  } catch {
    return false
  }
}

/**
 * The version of Inkpact that runs, and a newer release when there is one (run B.8). The server asks GitHub, once a day;
 * this asks the server once per signed-in person, and again only when someone else signs in or `load(true)` is asked.
 */
export const useVersionStore = defineStore('version', () => {
  const auth = useAuthStore()
  const info = ref<VersionInfo | null>(null)
  const error = ref<string | null>(null)
  const loading = ref(false)
  let loadedFor: string | null = null
  /** The request on its way, and whose it is: another person's answer is not theirs to wait for. */
  let pending: { userId: string; promise: Promise<void> } | null = null

  async function fetchInfo(userId: string) {
    loading.value = true
    error.value = null
    try {
      const answer = await api<VersionInfo>('GET', '/api/version')
      if (auth.user?.id !== userId) return
      info.value = {
        version: answer.version,
        development: answer.development,
        update: answer.update && isSafeLink(answer.update.url) ? answer.update : null,
      }
      loadedFor = userId
    } catch (caught) {
      if (auth.user?.id !== userId) return
      error.value = caught instanceof ApiError ? caught.code : 'unknown'
    } finally {
      if (pending?.userId === userId) loading.value = false
    }
  }

  /** Reads the version, unless it is known for the person signed in; `again` reads it anyway. */
  async function load(again = false) {
    const userId = auth.user?.id
    if (!userId) return
    if (loadedFor !== userId) {
      info.value = null
      error.value = null
    }
    if (!again && loadedFor === userId && info.value) return
    if (pending?.userId !== userId) {
      const promise = fetchInfo(userId).finally(() => {
        if (pending?.promise === promise) pending = null
      })
      pending = { userId, promise }
    }
    await pending.promise
  }

  return { info, error, loading, load }
})
