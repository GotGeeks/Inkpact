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
  update: AvailableUpdate | null
}

/** Release notes are only ever a release page of Inkpact on GitHub: anything else from the server is not shown. */
const RELEASE_PAGE = 'https://github.com/GotGeeks/Inkpact/releases/'

export function isReleasePage(url: string): boolean {
  try {
    const page = new URL(url)
    return (
      page.href.startsWith(RELEASE_PAGE) &&
      page.search === '' &&
      page.hash === '' &&
      page.username === '' &&
      page.password === ''
    )
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
  let pending: Promise<void> | null = null

  async function fetchInfo(userId: string) {
    loading.value = true
    error.value = null
    try {
      const answer = await api<VersionInfo>('GET', '/api/version')
      if (auth.user?.id !== userId) return
      info.value = {
        version: answer.version,
        update: answer.update && isReleasePage(answer.update.url) ? answer.update : null,
      }
      loadedFor = userId
    } catch (caught) {
      if (auth.user?.id !== userId) return
      error.value = caught instanceof ApiError ? caught.code : 'unknown'
    } finally {
      loading.value = false
    }
  }

  /** Reads the version, unless it is known for the person signed in; `again` reads it anyway. */
  async function load(again = false) {
    const userId = auth.user?.id
    if (!userId) return
    if (loadedFor !== userId) info.value = null
    if (!again && loadedFor === userId && info.value) return
    pending ??= fetchInfo(userId).finally(() => (pending = null))
    await pending
  }

  return { info, error, loading, load }
})
