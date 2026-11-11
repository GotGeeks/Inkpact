import { createHash } from 'node:crypto'
import { readFile, readdir, writeFile } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import type { Plugin, ResolvedConfig } from 'vite'

/**
 * The worker precaches the actual build, including lazy routes and the editor. Its version changes
 * when any cached file or the worker changes, so an unchanged worker cannot strand a new shell.
 */
export function offlineShell(): Plugin {
  let config: ResolvedConfig

  return {
    name: 'inkpact-offline-shell',
    apply: 'build',
    configResolved(resolved) {
      config = resolved
    },
    async closeBundle() {
      const output = resolve(config.root, config.build.outDir)
      const assets = (
        await readdir(join(output, 'assets'), { recursive: true, withFileTypes: true })
      )
        .filter((entry) => entry.isFile())
        .map(
          (entry) =>
            `/${join('assets', entry.parentPath.slice(join(output, 'assets').length), entry.name).replaceAll('\\', '/')}`,
        )
        .sort()
      const files = [
        '/index.html',
        '/manifest.webmanifest',
        '/icons/icon-192.png',
        '/icons/icon-512.png',
        ...assets,
      ]
      const template = await readFile(join(config.root, 'pwa', 'service-worker.js'), 'utf8')
      const hash = createHash('sha256').update(template)
      for (const file of files) {
        hash.update(file)
        hash.update(await readFile(join(output, file.slice(1))))
      }
      const worker = template
        .replace('__INKPACT_VERSION__', hash.digest('hex').slice(0, 20))
        .replace('__INKPACT_FILES__', JSON.stringify(files))
      await writeFile(join(output, 'service-worker.js'), worker)
    },
  }
}
