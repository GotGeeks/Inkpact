import { resolve } from 'node:path'

/** The administrator the setup project creates; every other project signs in as this account. */
export const ADMIN = {
  setupCode: 'E2ESETUP',
  email: 'admin@example.com',
  displayName: 'E2E Admin',
  password: 'correct horse battery staple',
  workspaceName: 'E2E workspace',
} as const

/** The signed-in browser state saved by the setup project. */
export const AUTH_STATE_FILE = resolve(import.meta.dirname, '.auth/admin.json')

export const SIGNED_OUT: { cookies: []; origins: [] } = { cookies: [], origins: [] }
