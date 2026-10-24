import { defineConfig } from 'vite'

// The dev server proxies /ws to the spike server, so the page and its socket share one origin.
export default defineConfig({
  server: {
    proxy: {
      '/ws': { target: 'ws://localhost:5080', ws: true },
    },
  },
})
