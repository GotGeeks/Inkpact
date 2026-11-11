import {
  createRouter,
  createWebHistory,
  type RouteLocationRaw,
  type Router,
  type RouterHistory,
} from 'vue-router'
import { ApiError } from './api'
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

/** The login page, coming back to `fullPath` after signing in (the start page needs no such note). */
export function loginRoute(fullPath?: string): RouteLocationRaw {
  return { name: 'login', query: fullPath && fullPath !== '/' ? { redirect: fullPath } : {} }
}

export function createAppRouter(history: RouterHistory = createWebHistory()): Router {
  const router = createRouter({
    history,
    routes: [
      {
        path: '/',
        // The sidebar and the page area: everything that needs a signed-in person lives inside it.
        component: () => import('./layout/AppShell.vue'),
        children: [
          { path: '', name: 'home', component: () => import('./pages/StartPage.vue') },
          { path: 'p/:pageId', name: 'page', component: () => import('./pages/PageView.vue') },
          { path: 'trash', name: 'trash', component: () => import('./pages/TrashView.vue') },
          { path: 'tags', name: 'tags', component: () => import('./tags/TagsView.vue') },
          { path: 'tags/:tagId', name: 'tag', component: () => import('./tags/TagView.vue') },
          {
            // The import's id is in the address, so that a reload shows the same import again.
            path: 'import/joplin/:importId?',
            name: 'import',
            component: () => import('./pages/ImportPage.vue'),
          },
        ],
      },
      { path: '/login', name: 'login', component: () => import('./pages/LoginPage.vue') },
      { path: '/setup', name: 'setup', component: () => import('./pages/SetupPage.vue') },
      {
        path: '/offline',
        name: 'offline',
        component: () => import('./offline/OfflineUnavailable.vue'),
      },
      { path: '/:pathMatch(.*)*', redirect: { name: 'home' } },
    ],
  })

  router.beforeEach(async (to): Promise<boolean | RouteLocationRaw> => {
    const auth = useAuthStore()
    if (!auth.loaded) {
      try {
        await auth.load()
      } catch (error) {
        if (!(error instanceof ApiError) || error.code !== 'network_error') throw error
        auth.enterOffline()
      }
    }

    // The cached identity only selects a local partition. Offline routes never grant access to server operations.
    if (auth.offline) {
      return to.name === 'page' || to.name === 'offline'
        ? true
        : { name: 'offline', query: { redirect: to.fullPath } }
    }

    if (to.name === 'offline') return safeRedirect(to.query.redirect) ?? { name: 'home' }

    if (auth.setupRequired) {
      return to.name === 'setup' ? true : { name: 'setup' }
    }

    if (!auth.user) {
      if (to.name === 'login') return true
      // The setup page is closed once an account exists.
      return loginRoute(to.name === 'setup' ? undefined : to.fullPath)
    }

    if (to.name === 'login' || to.name === 'setup') {
      return safeRedirect(to.query.redirect) ?? { name: 'home' }
    }

    return true
  })

  return router
}
