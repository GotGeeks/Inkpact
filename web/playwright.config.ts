import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { defineConfig, devices } from '@playwright/test'
import { ADMIN, AUTH_STATE_FILE } from './e2e/credentials'

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

// Every run starts from an empty installation: the setup project creates the administrator. The name is made
// once, in the process that starts the run, and the workers inherit it. Old databases are left behind in the
// development container; `docker compose -f deploy/docker-compose.dev.yml down -v` clears them.
process.env.E2E_RUN_ID ??= Date.now().toString(36)
const database =
  process.env.E2E_DATABASE ??
  `Host=127.0.0.1;Port=5432;Database=inkpact_e2e_${process.env.E2E_RUN_ID};Username=postgres;Password=postgres`
const keysDirectory = join(tmpdir(), `inkpact-e2e-keys-${process.env.E2E_RUN_ID}`)

const repoRoot = resolve(import.meta.dirname, '..')
const serverOutput = resolve(repoRoot, 'server/src/Inkpact.Api/bin/e2e')
// The server's content root is its output folder, which has no sidecar next to it as a published app has.
const sidecarScript = resolve(repoRoot, 'server/sidecar/src/main.ts')

// The same server under a name that is not localhost. Browsers treat localhost as a secure origin,
// so a policy that only breaks plain HTTP on a LAN address (upgrade-insecure-requests blanks every
// such install) never shows up there.
const plainHttpHost = 'inkpact.test'

const ignoredByDeviceProjects = /(auth\.setup|plain-http|logout-everywhere)\.(spec\.)?ts$/

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
    // Creates the administrator through the setup page, once, and saves the signed-in browser state.
    { name: 'setup', testMatch: /auth\.setup\.ts/, use: { ...devices['Desktop Chrome'] } },
    {
      name: 'desktop',
      testIgnore: ignoredByDeviceProjects,
      use: { ...devices['Desktop Chrome'], storageState: AUTH_STATE_FILE },
      dependencies: ['setup'],
    },
    {
      name: 'phone',
      testIgnore: ignoredByDeviceProjects,
      use: { ...devices['Pixel 7'], storageState: AUTH_STATE_FILE },
      dependencies: ['setup'],
    },
    {
      name: 'iphone',
      testIgnore: ignoredByDeviceProjects,
      use: { ...devices['iPhone 15'], storageState: AUTH_STATE_FILE },
      dependencies: ['setup'],
    },
    {
      name: 'plain-http',
      testMatch: /plain-http\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        baseURL: `http://${plainHttpHost}:${port}`,
        launchOptions: { args: [`--host-resolver-rules=MAP ${plainHttpHost} 127.0.0.1`] },
      },
      dependencies: ['setup'],
    },
    {
      // Ends every session of the administrator, so it runs when everything else is done.
      name: 'lifecycle',
      testMatch: /logout-everywhere\.spec\.ts/,
      use: { ...devices['Desktop Chrome'] },
      dependencies: ['desktop', 'phone', 'iphone', 'plain-http'],
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
      ConnectionStrings__Inkpact: database,
      Database__BackupOnMigrate: 'false',
      Auth__SetupCode: ADMIN.setupCode,
      // The suite signs in about forty times from one address; the real limit (10 a minute) would stop it.
      // The lockout per email stays at its real value: one spec locks an address on purpose.
      Auth__Throttle__PerAddressPerMinute: '600',
      DataProtection__KeysDirectory: keysDirectory,
      Sidecar__ScriptPath: sidecarScript,
    },
  },
})
