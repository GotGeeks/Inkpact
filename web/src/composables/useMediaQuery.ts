import { onBeforeUnmount, ref, type Ref } from 'vue'

/**
 * Whether a media query matches, kept up to date. Where the browser has no matchMedia (a test runner), it is the
 * fallback.
 */
export function useMediaQuery(query: string, fallback = false): Ref<boolean> {
  const matches = ref(fallback)
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return matches

  const list = window.matchMedia(query)
  matches.value = list.matches
  const update = (event: MediaQueryListEvent) => {
    matches.value = event.matches
  }
  list.addEventListener('change', update)
  onBeforeUnmount(() => list.removeEventListener('change', update))
  return matches
}
