import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { runInNewContext } from 'node:vm'
import { describe, expect, it, vi } from 'vitest'

// The worker as the build writes it, without its list of files: run against a stand-in for its global scope.
const source = readFileSync(
  resolve(import.meta.dirname, '../../pwa/service-worker.js'),
  'utf8',
).replace('__INKPACT_FILES__', '[]')

const appOrigin = 'https://notes.example'

function startWorker() {
  const listeners = new Map<string, (event: unknown) => void>()
  const self = {
    location: { origin: appOrigin },
    addEventListener: (type: string, listener: (event: unknown) => void) =>
      listeners.set(type, listener),
    clients: { matchAll: vi.fn().mockResolvedValue([{ id: 'tab' }]) },
    skipWaiting: vi.fn().mockResolvedValue(undefined),
  }
  runInNewContext(source, { self, caches: {} })

  async function send(origin: string) {
    const pending: Promise<unknown>[] = []
    listeners.get('message')!({
      origin,
      data: { type: 'skip-waiting' },
      source: { id: 'tab', postMessage: vi.fn() },
      waitUntil: (promise: Promise<unknown>) => pending.push(promise),
    })
    await Promise.all(pending)
  }

  return { self, send }
}

describe('service worker messages', () => {
  it('takes the update when a tab of the app asks', async () => {
    const { self, send } = startWorker()
    await send(appOrigin)
    expect(self.skipWaiting).toHaveBeenCalledOnce()
  })

  it('ignores a message from another origin', async () => {
    const { self, send } = startWorker()
    await send('https://elsewhere.example')
    expect(self.clients.matchAll).not.toHaveBeenCalled()
    expect(self.skipWaiting).not.toHaveBeenCalled()
  })
})
