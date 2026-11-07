<script setup lang="ts">
import { computed, nextTick, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import { PAGE_MENU_LISTBOX_ID, pageOptionId, type PageMenuState } from './pageLinkExtension'
import { sharedTitles } from './pageSearch'
import { useMenuPlacement } from './useMenuPlacement'

const props = defineProps<{
  state: PageMenuState
  /** Room to leave at the bottom of the screen, for the toolbar. */
  reserveBottom: number
}>()

const { t } = useI18n()
const menu = useTemplateRef<HTMLElement>('menu')
const { placed, position } = useMenuPlacement({
  menu,
  open: () => props.state.open,
  anchor: () => props.state.anchor,
  reserveBottom: () => props.reserveBottom,
  changes: () => [props.state.items.length],
})

/** The pages with a title that another in the list has too: they say which page they are in. */
const shared = computed(() => sharedTitles(props.state.items))

// The chosen option stays in view while the arrow keys move through the list.
watch(
  () => props.state.selected,
  async () => {
    await nextTick()
    const chosen = props.state.items[props.state.selected]
    if (chosen)
      menu.value?.querySelector(`#${pageOptionId(chosen)}`)?.scrollIntoView?.({ block: 'nearest' })
  },
)
</script>

<template>
  <div
    v-if="state.open"
    ref="menu"
    data-testid="page-link-menu"
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
          ? t('editor.pageLink.results', { count: state.items.length }, state.items.length)
          : t('editor.pageLink.empty')
      }}
    </p>
    <p
      v-if="!state.items.length"
      class="px-space-md py-space-sm text-body-sm text-on-surface-variant"
    >
      {{ t('editor.pageLink.empty') }}
    </p>
    <div
      v-show="state.items.length"
      :id="PAGE_MENU_LISTBOX_ID"
      role="listbox"
      :aria-label="t('editor.pageLink.label')"
    >
      <!-- The mouse must not take the focus from the editor, or the menu closes before the click. -->
      <div
        v-for="(item, index) in state.items"
        :id="pageOptionId(item)"
        :key="item.id"
        role="option"
        :aria-selected="index === state.selected"
        class="flex min-h-11 cursor-pointer items-center gap-space-md rounded-md px-space-md py-space-sm"
        :class="index === state.selected ? 'bg-surface-elevated text-primary' : 'text-on-surface'"
        @mousedown.prevent
        @mousemove="state.point(index)"
        @click="state.choose(item)"
      >
        <AppIcon name="page" class="size-5 shrink-0" />
        <span class="flex min-w-0 flex-col">
          <span class="truncate text-body-sm">{{ item.title || t('tree.untitled') }}</span>
          <span
            v-if="shared.has(item.id) && item.parentTitle"
            class="truncate text-label-md text-on-surface-variant"
          >
            {{ t('editor.pageLink.in', { parent: item.parentTitle }) }}
          </span>
        </span>
      </div>
    </div>
  </div>
</template>
