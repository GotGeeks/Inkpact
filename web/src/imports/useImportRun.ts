import { onScopeDispose, ref, watch, type Ref } from 'vue'
import { ApiError } from '../api'
import { getImport, hasEnded, type ImportRun } from './importApi'

/** Reads that fail in a row before the page says the connection is lost. */
const LOST_AFTER = 3

/**
 * Follows an import: reads it now and again every `intervalMs` until it has ended. A failed read is tried again, since
 * the server may only be restarting; only an answer that will not change (the import is not there, or is not yours)
 * ends the polling with its code in `error`. Changing the id starts over, and leaving the page stops it.
 */
export function useImportRun(
  id: Ref<string | null>,
  options: { intervalMs?: number; onEnded?: (run: ImportRun) => void } = {},
) {
  const intervalMs = options.intervalMs ?? 1000
  const run = ref<ImportRun | null>(null)
  const error = ref<string | null>(null)
  const connectionLost = ref(false)

  let timer: ReturnType<typeof setTimeout> | undefined
  // Each start gets a number, so that an answer that arrives after the id changed (or the page closed) is ignored.
  let generation = 0
  let failures = 0

  async function poll(current: string, mine: number) {
    let delay = intervalMs
    try {
      const next = await getImport(current)
      if (mine !== generation) return
      run.value = next
      error.value = null
      failures = 0
      connectionLost.value = false
      if (hasEnded(next)) {
        options.onEnded?.(next)
        return
      }
    } catch (e) {
      if (mine !== generation) return
      // Not found, not yours, signed out: asking again gives the same answer. A rate limit or a timeout passes.
      const fixed =
        e instanceof ApiError &&
        e.status >= 400 &&
        e.status < 500 &&
        e.status !== 408 &&
        e.status !== 429
      if (fixed) {
        error.value = e.code
        return
      }
      if (e instanceof ApiError && e.retryAfterSeconds !== undefined) {
        // The server is there and said when to ask again: waiting is not a lost connection.
        delay = Math.max(intervalMs, e.retryAfterSeconds * 1000)
      } else {
        failures++
        if (failures >= LOST_AFTER) connectionLost.value = true
      }
    }
    timer = setTimeout(() => void poll(current, mine), delay)
  }

  function stop() {
    generation++
    clearTimeout(timer)
  }

  watch(
    id,
    (current) => {
      stop()
      run.value = null
      error.value = null
      connectionLost.value = false
      failures = 0
      if (current) void poll(current, generation)
    },
    { immediate: true },
  )

  onScopeDispose(stop)

  return { run, error, connectionLost }
}
