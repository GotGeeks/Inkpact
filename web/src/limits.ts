import { api } from './api'

/**
 * The largest file an endpoint takes, asked once and kept. Unknown (null) when it could not be asked; then the server
 * decides, and the next call asks again.
 */
export function cachedLimit(path: string) {
  let limit: Promise<number | null> | null = null

  function get(): Promise<number | null> {
    limit ??= api<{ maxFileSize: number }>('GET', path).then(
      (limits) => limits.maxFileSize,
      () => {
        limit = null
        return null
      },
    )
    return limit
  }

  /** For the tests: ask the server again. */
  function forget() {
    limit = null
  }

  return { get, forget }
}
