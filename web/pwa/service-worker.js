/* The build replaces these values with the version and the complete list of static files. */
const cacheName = 'inkpact-shell-__INKPACT_VERSION__'
const files = __INKPACT_FILES__
const staticFiles = new Set(files)

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(cacheName).then((cache) => cache.addAll(files)))
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
      if (windows.length > 0) return

      const names = await caches.keys()
      await Promise.all(
        names
          .filter((name) => name.startsWith('inkpact-shell-') && name !== cacheName)
          .map((name) => caches.delete(name)),
      )
    })(),
  )
})

self.addEventListener('fetch', (event) => {
  const { request } = event
  const url = new URL(request.url)
  if (
    request.method !== 'GET' ||
    url.origin !== self.location.origin ||
    url.pathname === '/api' ||
    url.pathname.startsWith('/api/')
  )
    return

  const appRoute =
    /^\/(?:$|p\/[^/]+\/?$|trash\/?$|tags(?:\/[^/]+)?\/?$|import\/joplin(?:\/[^/]+)?\/?$|login\/?$|setup\/?$|offline\/?$)/.test(
      url.pathname,
    )
  if (request.mode === 'navigate' && appRoute) {
    // A controlled tab must keep the shell that matches its worker's cached chunks.
    event.respondWith(
      (async () => {
        const cache = await caches.open(cacheName)
        return (await cache.match('/index.html')) ?? fetch(request)
      })(),
    )
  } else if (staticFiles.has(url.pathname)) {
    event.respondWith(
      (async () => {
        const cache = await caches.open(cacheName)
        return (await cache.match(url.pathname)) ?? fetch(request)
      })(),
    )
  }
})
