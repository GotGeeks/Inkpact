import { onBeforeUnmount, onMounted } from 'vue'

/**
 * Calls `callback` each time the tab comes back into view (another device may have changed what it shows). Returns a
 * function that stops it. For a store, which has no lifecycle of its own.
 */
export function onTabVisible(callback: () => void): () => void {
  const listener = () => {
    if (document.visibilityState === 'visible') callback()
  }
  document.addEventListener('visibilitychange', listener)
  return () => document.removeEventListener('visibilitychange', listener)
}

/** {@link onTabVisible} for as long as the component is mounted. */
export function useOnTabVisible(callback: () => void) {
  let stop: (() => void) | null = null
  onMounted(() => (stop = onTabVisible(callback)))
  onBeforeUnmount(() => stop?.())
}
