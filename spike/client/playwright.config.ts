import { defineConfig } from '@playwright/test'

// The e2e suite runs its own server (so it can restart it) and its own Vite on separate ports,
// leaving a dev setup on 5080/5173 alone. It needs the spike Postgres from spike/docker-compose.yml.
export const E2E_SERVER_PORT = 5081
export const E2E_CLIENT_PORT = 5174

export default defineConfig({
  testDir: 'e2e',
  // Scenarios restart the shared server, so they must not run side by side.
  workers: 1,
  fullyParallel: false,
  timeout: 60_000,
  reporter: [['list']],
  use: {
    baseURL: `http://localhost:${E2E_CLIENT_PORT}`,
    browserName: 'chromium',
  },
  webServer: {
    command: 'node node_modules/vite/bin/vite.js --port 5174 --strictPort',
    url: `http://localhost:${E2E_CLIENT_PORT}`,
    env: { SPIKE_SERVER: `ws://localhost:${E2E_SERVER_PORT}` },
    reuseExistingServer: false,
  },
})
