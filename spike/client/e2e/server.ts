import { spawn, type ChildProcess } from 'node:child_process'
import { openSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const dll = fileURLToPath(new URL('../../server/Spike.Server/bin/e2e/Spike.Server.dll', import.meta.url))
const POSTGRES = 'Host=127.0.0.1;Port=5434;Username=spike;Password=spike;Database=inkpact_spike'

/** The spike server as a child process, so a test can kill it mid-edit and start it again. */
export class ServerProcess {
  private child: ChildProcess | null = null
  private readonly port: number

  constructor(port: number) {
    this.port = port
  }

  get httpUrl() {
    return `http://127.0.0.1:${this.port}`
  }

  async start() {
    if (this.child) throw new Error('Server already running')
    this.child = spawn('dotnet', [dll, '--urls', this.httpUrl, `--ConnectionStrings:Spike=${POSTGRES}`, '--Compaction:Enabled=false'], {
      env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' },
      // Set E2E_SERVER_LOG to a file path to keep the server output.
      stdio: process.env.E2E_SERVER_LOG ? ['ignore', openSync(process.env.E2E_SERVER_LOG, 'a'), 'inherit'] : 'ignore',
    })
    this.child.once('exit', () => (this.child = null))

    const deadline = Date.now() + 30_000
    while (Date.now() < deadline) {
      if (!this.child) throw new Error('Server exited during startup')
      try {
        const response = await fetch(`${this.httpUrl}/debug/pages/startup-probe/updates`)
        if (response.ok) return
      } catch {
        // Not listening yet.
      }
      await new Promise((resolve) => setTimeout(resolve, 100))
    }
    throw new Error('Server did not become ready')
  }

  async stop() {
    const child = this.child
    if (!child) return
    const exited = new Promise((resolve) => child.once('exit', resolve))
    child.kill()
    await exited
  }

  /** Number of update rows stored for a page. */
  async storedUpdates(pageId: string): Promise<number> {
    const response = await fetch(`${this.httpUrl}/debug/pages/${pageId}/updates`)
    return ((await response.json()) as { count: number }).count
  }
}
