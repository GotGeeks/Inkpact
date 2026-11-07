<script setup lang="ts">
import { computed, useTemplateRef } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import type { BlockCategory, BlockCommand } from './blockCommands'
import { SLASH_LISTBOX_ID, slashOptionId, type SlashState } from './slashExtension'
import { useChosenInView, useMenuPlacement } from './useMenuPlacement'

const props = defineProps<{
  state: SlashState
  /** Room to leave at the bottom of the screen, for the toolbar. */
  reserveBottom: number
}>()

const { t, n } = useI18n()
const menu = useTemplateRef<HTMLElement>('menu')
const { placed, position } = useMenuPlacement({
  menu,
  open: () => props.state.open,
  anchor: () => props.state.anchor,
  reserveBottom: () => props.reserveBottom,
  changes: () => [props.state.items.length],
})

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

useChosenInView(menu, () => {
  const chosen = props.state.items[props.state.selected]
  return chosen ? slashOptionId(chosen) : null
})
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
          ? t('editor.slash.results', { count: n(state.items.length) }, state.items.length)
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
