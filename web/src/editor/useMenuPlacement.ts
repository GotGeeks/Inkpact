import { computePosition, flip, offset, shift, size } from '@floating-ui/dom'
import { nextTick, reactive, ref, watch, type Ref } from 'vue'
import type { AnchorRect } from './slashExtension'

/**
 * Where a menu that opens at the caret goes: under it, or above when there is no room, inside the part of the screen that is
 * visible (with the keyboard open it is smaller than the layout viewport), leaving `reserveBottom` for the toolbar. The slash
 * menu and the page menu share it. The position goes through the style object (CSSOM), which the strict policy allows; a
 * `style` attribute it does not. The menu is hidden until it has a place, so it does not flash in a corner of the screen first.
 */
export function useMenuPlacement(options: {
  menu: Ref<HTMLElement | null>
  open: () => boolean
  anchor: () => AnchorRect | null
  reserveBottom: () => number
  /** What makes the menu be placed again: its content, besides where it is anchored. */
  changes: () => unknown[]
}) {
  const placed = ref(false)
  const position = reactive({ left: 0, top: 0, maxHeight: 320 })

  async function place() {
    const el = options.menu.value
    const anchor = options.anchor()
    if (!el || !anchor) return
    const view = window.visualViewport
    const boundary = {
      x: view?.offsetLeft ?? 0,
      y: view?.offsetTop ?? 0,
      width: view?.width ?? window.innerWidth,
      height: (view?.height ?? window.innerHeight) - options.reserveBottom(),
    }
    const caret = {
      getBoundingClientRect: () => ({
        x: anchor.left,
        y: anchor.top,
        left: anchor.left,
        top: anchor.top,
        right: anchor.right,
        bottom: anchor.bottom,
        width: anchor.right - anchor.left,
        height: anchor.bottom - anchor.top,
      }),
    }
    const result = await computePosition(caret, el, {
      strategy: 'fixed',
      placement: 'bottom-start',
      middleware: [
        offset(4),
        flip({ rootBoundary: boundary, padding: 8 }),
        shift({ rootBoundary: boundary, padding: 8 }),
        size({
          rootBoundary: boundary,
          padding: 8,
          apply: ({ availableHeight }) => {
            position.maxHeight = Math.max(120, Math.min(320, availableHeight))
          },
        }),
      ],
    })
    position.left = result.x
    position.top = result.y
    placed.value = true
  }

  watch(
    () => [options.open(), options.anchor(), options.reserveBottom(), ...options.changes()],
    async () => {
      if (!options.open()) placed.value = false
      await nextTick()
      await place()
    },
    { flush: 'post' },
  )

  return { placed, position }
}
