<script setup lang="ts">
import { computePosition, flip, offset, shift, size } from '@floating-ui/dom'
import { computed, nextTick, reactive, ref, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import type { BlockCategory, BlockCommand } from './blockCommands'
import { SLASH_LISTBOX_ID, slashOptionId, type SlashState } from './slashExtension'

const props = defineProps<{
  state: SlashState
  /** Room to leave at the bottom of the screen, for the toolbar. */
  reserveBottom: number
}>()

const { t } = useI18n()
const menu = useTemplateRef<HTMLElement>('menu')
// Positions go through the style object (CSSOM), which the strict policy allows; a `style` attribute it does not.
// Hidden until it has a place, so it does not flash in a corner of the screen first.
const placed = ref(false)
const position = reactive({ left: 0, top: 0, maxHeight: 320 })

const categories: BlockCategory[] = ['basic', 'lists', 'blocks']
function categoryLabel(category: BlockCategory): string {
  if (category === 'basic') return t('editor.slash.categoryBasic')
  if (category === 'lists') return t('editor.slash.categoryLists')
  return t('editor.slash.categoryBlocks')
}

/** The items under their header, in the order of the list; an empty category gets no header. */
const groups = computed(() =>
  categories
    .map((category) => ({
      category,
      items: props.state.items.filter((item) => item.category === category),
    }))
    .filter((group) => group.items.length > 0),
)

function indexOf(item: BlockCommand): number {
  return props.state.items.indexOf(item)
}

async function place() {
  const el = menu.value
  const anchor = props.state.anchor
  if (!el || !anchor) return
  // The visible part of the screen: with the keyboard open it is smaller than the layout viewport.
  const view = window.visualViewport
  const boundary = {
    x: view?.offsetLeft ?? 0,
    y: view?.offsetTop ?? 0,
    width: view?.width ?? window.innerWidth,
    height: (view?.height ?? window.innerHeight) - props.reserveBottom,
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
  () => [props.state.open, props.state.anchor, props.state.items.length, props.reserveBottom],
  async () => {
    if (!props.state.open) placed.value = false
    await nextTick()
    await place()
  },
  { flush: 'post' },
)

// The chosen option stays in view while the arrow keys move through a long list.
watch(
  () => props.state.selected,
  async () => {
    await nextTick()
    const chosen = props.state.items[props.state.selected]
    if (chosen)
      menu.value?.querySelector(`#${slashOptionId(chosen)}`)?.scrollIntoView?.({ block: 'nearest' })
  },
)
</script>

<template>
  <div
    v-if="state.open"
    ref="menu"
    data-testid="slash-menu"
    class="fixed z-50 w-72 max-w-[calc(100vw-1rem)] overflow-y-auto rounded-lg border border-border-subtle bg-surface-subtle p-space-xs text-on-surface shadow-lg"
    :class="{ invisible: !placed }"
    :style="{
      left: `${position.left}px`,
      top: `${position.top}px`,
      maxHeight: `${position.maxHeight}px`,
    }"
  >
    <!-- Spoken when the list changes; the editor keeps the focus, so nothing else announces it. -->
    <p role="status" class="sr-only">
      {{
        state.items.length
          ? t('editor.slash.results', { count: state.items.length }, state.items.length)
          : t('editor.slash.empty')
      }}
    </p>
    <p
      v-if="!state.items.length"
      class="px-space-md py-space-sm text-body-sm text-on-surface-variant"
    >
      {{ t('editor.slash.empty') }}
    </p>
    <div
      v-show="state.items.length"
      :id="SLASH_LISTBOX_ID"
      role="listbox"
      :aria-label="t('editor.slash.label')"
    >
      <div
        v-for="group in groups"
        :key="group.category"
        role="group"
        :aria-labelledby="`slash-group-${group.category}`"
      >
        <p
          :id="`slash-group-${group.category}`"
          class="px-space-md pt-space-sm pb-space-xs text-label-xs text-on-surface-variant uppercase"
        >
          {{ categoryLabel(group.category) }}
        </p>
        <!-- The mouse must not take the focus from the editor, or the menu closes before the click. -->
        <div
          v-for="item in group.items"
          :id="slashOptionId(item)"
          :key="item.id"
          role="option"
          :aria-selected="indexOf(item) === state.selected"
          class="flex min-h-11 cursor-pointer items-center gap-space-md rounded-md px-space-md py-space-sm"
          :class="
            indexOf(item) === state.selected
              ? 'bg-surface-elevated text-primary'
              : 'text-on-surface'
          "
          @mousedown.prevent
          @mousemove="state.point(indexOf(item))"
          @click="state.choose(item)"
        >
          <AppIcon :name="item.icon" class="size-5" />
          <span class="flex min-w-0 flex-col">
            <span class="text-body-sm">{{ item.label(t) }}</span>
            <span class="truncate text-label-md text-on-surface-variant">{{
              item.description(t)
            }}</span>
          </span>
        </div>
      </div>
    </div>
  </div>
</template>
