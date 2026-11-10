import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, setCsrfToken } from '../api'
import { readOfflineIdentity, writeOfflineIdentity } from '../offline/identity'

export interface CurrentUser {
  id: string
  email: string
  displayName: string
  isAdmin: boolean
}

export interface AuthState {
  setupRequired: boolean
  user: CurrentUser | null
  csrfToken: string | null
}

export interface SetupForm {
  setupCode: string
  email: string
  displayName: string
  password: string
  workspaceName: string
}

export const useAuthStore = defineStore('auth', () => {
  const loaded = ref(false)
  const setupRequired = ref(false)
  const user = ref<CurrentUser | null>(null)
  const offline = ref(false)
  const offlineUserId = ref<string | null>(null)
  const offlineIdentityFailed = ref(false)

  function acceptState(state: AuthState) {
    setupRequired.value = state.setupRequired
    user.value = state.user
    setCsrfToken(state.csrfToken)
    offlineIdentityFailed.value = !writeOfflineIdentity(state.user?.id ?? null)
    offlineUserId.value = null
    offline.value = false
    loaded.value = true
  }

  async function readState(): Promise<AuthState> {
    return api<AuthState>('GET', '/api/auth/state')
  }

  async function load() {
    acceptState(await readState())
  }

  /** Used only when the server could not be reached, never for a 401, 403, or a different account. */
  function enterOffline() {
    offlineUserId.value = readOfflineIdentity()
    user.value = null
    setCsrfToken(null)
    offline.value = true
    loaded.value = true
  }

  async function setup(form: SetupForm) {
    await api('POST', '/api/auth/setup', form)
    await load()
  }

  async function login(email: string, password: string) {
    await api('POST', '/api/auth/login', { email, password })
    await load()
  }

  async function logout() {
    await api('POST', '/api/auth/logout')
    acceptState({ setupRequired: setupRequired.value, user: null, csrfToken: null })
    await load()
  }

  async function logoutEverywhere() {
    await api('POST', '/api/auth/logout-everywhere')
    acceptState({ setupRequired: setupRequired.value, user: null, csrfToken: null })
    await load()
  }

  return {
    loaded,
    setupRequired,
    user,
    offline,
    offlineUserId,
    offlineIdentityFailed,
    readState,
    acceptState,
    enterOffline,
    load,
    setup,
    login,
    logout,
    logoutEverywhere,
  }
})
