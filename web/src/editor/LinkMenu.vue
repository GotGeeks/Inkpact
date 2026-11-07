<script setup lang="ts">
import { autoUpdate, computePosition, flip, offset, shift } from '@floating-ui/dom'
import type { Editor } from '@tiptap/core'
import { computed, nextTick, onBeforeUnmount, reactive, ref, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { isAllowedLink, pageIdOfLink } from './links'

const props = defineProps<{
  editor: Editor
  /** Opens the address: a page of this app in this tab, anything else in a new one (`openLinkIn`). */
  open: (href: string, newTab: boolean) => void
}>()

const emit = defineEmits<{
  /** Asks for the picker on the whole link, to point it somewhere else. */
  change: []
}>()

/** Takes the link away from the whole link; the text stays and the caret stays in it. */
function remove() {
  props.editor.chain().focus().unsetLink().run()
}

const { t } = useI18n()
const menu = useTemplateRef<HTMLElement>('menu')
const position = reactive({ left: 0, top: 0 })
const placed = ref(false)

/** The address of the link the caret is in, when the editor allows it; what another client wrote is checked here too. */
const href = computed(() => {
  // Read with the selection, so the menu follows the caret from one link to the next.
  void props.editor.state.selection
  const value = props.editor.getAttributes('link').href as unknown
  return typeof value === 'string' && isAllowedLink(value) ? value : null
})

const label = computed(() =>
  href.value && pageIdOfLink(href.value) ? t('editor.link.openPage') : t('editor.link.open'),
)

/** The link the caret is in, as it is on the page. */
function linkElement(): HTMLElement | null {
  const { node } = props.editor.view.domAtPos(props.editor.state.selection.from)
  const element = node instanceof HTMLElement ? node : node.parentElement
  return element?.closest('a') ?? null
}

let stopFollowing: (() => void) | null = null
async function follow() {
  stopFollowing?.()
  stopFollowing = null
  await nextTick()
  const link = linkElement()
  if (!menu.value || !link) return
  const el = menu.value
  stopFollowing = autoUpdate(link, el, async () => {
    const result = await computePosition(link, el, {
      strategy: 'fixed',
      placement: 'bottom-start',
      middleware: [offset(4), flip(), shift({ padding: 8 })],
    })
    position.left = result.x
    position.top = result.y
    placed.value = true
  })
}

watch(() => props.editor.state.selection.from, follow, { immediate: true, flush: 'post' })
onBeforeUnmount(() => stopFollowing?.())
</script>

<template>
  <div
    v-if="href"
    ref="menu"
    role="toolbar"
    data-editor-chrome
    data-testid="link-menu"
    :aria-label="t('editor.link.label')"
    class="fixed z-40 flex max-w-[calc(100vw-1rem)] rounded-lg border border-border-subtle bg-surface-subtle p-space-xs text-body-sm text-on-surface shadow-lg"
    :class="{ invisible: !placed }"
    :style="{ left: `${position.left}px`, top: `${position.top}px` }"
  >
    <!-- The mouse must not take the focus from the text, or the caret that says which link this is would be lost. -->
    <button
      type="button"
      data-testid="link-open"
      class="min-h-9 shrink-0 rounded-md px-space-md whitespace-nowrap outline-none hover:bg-surface-elevated focus-visible:ring-2 focus-visible:ring-primary pointer-coarse:min-h-11"
      @mousedown.prevent
      @click="open(href!, false)"
    >
      {{ label }}
    </button>
    <button
      type="button"
      data-testid="link-change"
      class="min-h-9 shrink-0 rounded-md px-space-md whitespace-nowrap outline-none hover:bg-surface-elevated focus-visible:ring-2 focus-visible:ring-primary pointer-coarse:min-h-11"
      @mousedown.prevent
      @click="emit('change')"
    >
      {{ t('editor.link.change') }}
    </button>
    <button
      type="button"
      data-testid="link-remove"
      class="min-h-9 shrink-0 rounded-md px-space-md whitespace-nowrap outline-none hover:bg-surface-elevated focus-visible:ring-2 focus-visible:ring-primary pointer-coarse:min-h-11"
      @mousedown.prevent
      @click="remove"
    >
      {{ t('editor.link.remove') }}
    </button>
  </div>
</template>
