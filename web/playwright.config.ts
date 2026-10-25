import { resolve } from 'node:path'
import { defineConfig, devices } from '@playwright/test'

/**
 * The suite runs against the real server serving the production bundle, not the Vite dev server:
 * the Content-Security-Policy only exists there, and a change that Vite, the unit tests and the type
 * checker all accept can still break the shipped app.
 *
 * `pnpm e2e` builds the bundle and the server, and starts it on its own port, so a dev server on
 * :5036 can keep running. Needs Postgres:
 *   docker compose -f deploy/docker-compose.dev.yml up -d --wait
 */
const port = 5090
const baseURL = `http://127.0.0.1:${port}`

const repoRoot = resolve(import.meta.dirname, '..')
const serverOutput = resolve(repoRoot, 'server/src/Inkpact.Api/bin/e2e')

// The same server under a name that is not localhost. Browsers treat localhost as a secure origin,
// so a policy that only breaks plain HTTP on a LAN address (upgrade-insecure-requests blanks every
// such install) never shows up there.
const plainHttpHost = 'inkpact.test'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'desktop',
      testIgnore: /plain-http\.spec\.ts/,
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'phone',
      testIgnore: /plain-http\.spec\.ts/,
      use: { ...devices['Pixel 7'] },
    },
    {
      name: 'iphone',
      testIgnore: /plain-http\.spec\.ts/,
      use: { ...devices['iPhone 15'] },
    },
    {
      name: 'plain-http',
      testMatch: /plain-http\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        baseURL: `http://${plainHttpHost}:${port}`,
        launchOptions: { args: [`--host-resolver-rules=MAP ${plainHttpHost} 127.0.0.1`] },
      },
    },
  ],
  webServer: {
    // Build the bundle and the server (to its own folder, so a running dev server is not locked), then run it.
    command: [
      'pnpm build',
      `dotnet build ../server/src/Inkpact.Api --configuration Release --output "${serverOutput}" --verbosity quiet`,
      `dotnet "${resolve(serverOutput, 'Inkpact.Api.dll')}"`,
    ].join(' && '),
    url: `${baseURL}/api/health`,
    timeout: 240_000,
    reuseExistingServer: !process.env.CI,
    stdout: 'pipe',
    stderr: 'pipe',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: baseURL,
      ASPNETCORE_CONTENTROOT: serverOutput,
      ASPNETCORE_WEBROOT: resolve(import.meta.dirname, 'dist'),
      ConnectionStrings__Inkpact:
        process.env.E2E_DATABASE ??
        'Host=127.0.0.1;Port=5432;Database=inkpact_e2e;Username=postgres;Password=postgres',
      Database__BackupOnMigrate: 'false',
    },
  },
})
