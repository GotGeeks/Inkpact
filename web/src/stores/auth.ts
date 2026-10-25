import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '../api'

export interface CurrentUser {
  id: string
  email: string
  displayName: string
  isAdmin: boolean
}

interface AuthState {
  setupRequired: boolean
  user: CurrentUser | null
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

  async function load() {
    const state = await api<AuthState>('GET', '/api/auth/state')
    setupRequired.value = state.setupRequired
    user.value = state.user
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
    await load()
  }

  async function logoutEverywhere() {
    await api('POST', '/api/auth/logout-everywhere')
    await load()
  }

  return { loaded, setupRequired, user, load, setup, login, logout, logoutEverywhere }
})
