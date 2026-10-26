import { onBeforeUnmount, ref, watch, type Ref } from 'vue'

const PROPERTY = '--keyboard-inset'

/**
 * How far the on-screen keyboard covers the bottom of the page, in pixels, while `active`. The layout viewport does not
 * shrink when the keyboard opens (on iOS, and on Android Chrome by default), only the visual viewport does, so this is
 * the layout height minus the visual viewport's height and its offset. Written as `--keyboard-inset` on the document
 * element (through the style object, which the strict policy allows) for whatever has to sit above the keyboard, and
 * removed again when the editor loses focus. Zero where there is no visual viewport.
 */
export function useKeyboardInset(active: Ref<boolean>): Ref<number> {
  const inset = ref(0)
  const viewport = typeof window === 'undefined' ? null : (window.visualViewport ?? null)

  function update() {
    if (!viewport) return
    const covered = document.documentElement.clientHeight - viewport.height - viewport.offsetTop
    inset.value = Math.max(0, Math.round(covered))
    document.documentElement.style.setProperty(PROPERTY, `${inset.value}px`)
  }

  function stop() {
    viewport?.removeEventListener('resize', update)
    viewport?.removeEventListener('scroll', update)
    document.documentElement.style.removeProperty(PROPERTY)
    inset.value = 0
  }

  watch(
    active,
    (on) => {
      if (!viewport) return
      if (on) {
        viewport.addEventListener('resize', update)
        viewport.addEventListener('scroll', update)
        update()
      } else stop()
    },
    { immediate: true },
  )
  onBeforeUnmount(stop)
  return inset
}
