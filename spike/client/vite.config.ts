import { defineConfig } from 'vite'

// The dev server proxies /ws to the spike server, so the page and its socket share one origin.
// The e2e tests point it at their own server instance through SPIKE_SERVER.
export default defineConfig({
  server: {
    proxy: {
      '/ws': { target: process.env.SPIKE_SERVER ?? 'ws://localhost:5080', ws: true },
    },
  },
})
