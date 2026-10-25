import {
  createRouter,
  createWebHistory,
  type RouteLocationRaw,
  type Router,
  type RouterHistory,
} from 'vue-router'
import { useAuthStore } from './stores/auth'

/**
 * Where to go after signing in: only a path inside this app. Anything else, such as "//evil.example",
 * "/\evil.example" or "https://evil.example", is ignored, so the login page cannot be used to send
 * someone to another site.
 */
export function safeRedirect(value: unknown): string | null {
  if (typeof value !== 'string') return null
  if (!/^\/(?![/\\])/.test(value)) return null
  if (/[\u0000-\u001f\\]/.test(value)) return null
  if (/^\/(login|setup)(\/|\?|#|$)/.test(value)) return null
  return value
}

export function createAppRouter(history: RouterHistory = createWebHistory()): Router {
  const router = createRouter({
    history,
    routes: [
      { path: '/', name: 'home', component: () => import('./pages/HomePage.vue') },
      { path: '/login', name: 'login', component: () => import('./pages/LoginPage.vue') },
      { path: '/setup', name: 'setup', component: () => import('./pages/SetupPage.vue') },
      { path: '/:pathMatch(.*)*', redirect: { name: 'home' } },
    ],
  })

  router.beforeEach(async (to): Promise<boolean | RouteLocationRaw> => {
    const auth = useAuthStore()
    if (!auth.loaded) await auth.load()

    if (auth.setupRequired) {
      return to.name === 'setup' ? true : { name: 'setup' }
    }

    if (!auth.user) {
      if (to.name === 'login') return true
      // The setup page is closed once an account exists.
      const redirect = to.name === 'setup' ? undefined : to.fullPath
      return { name: 'login', query: redirect && redirect !== '/' ? { redirect } : {} }
    }

    if (to.name === 'login' || to.name === 'setup') {
      return safeRedirect(to.query.redirect) ?? { name: 'home' }
    }

    return true
  })

  return router
}
