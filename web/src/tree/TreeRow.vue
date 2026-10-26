<script setup lang="ts">
import {
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuPortal,
  DropdownMenuRoot,
  DropdownMenuTrigger,
} from 'reka-ui'
import { nextTick, onMounted, ref, useTemplateRef } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import type { Row, DropZone } from './treeModel'

const props = defineProps<{
  row: Row
  selected: boolean
  /** The one row that can be reached with the Tab key; the arrow keys move between the rows. */
  tabbable: boolean
  renaming: boolean
  menuOpen: boolean
  canEdit: boolean
  dropZone: DropZone | null
  dragging: boolean
  draggable: boolean
}>()

const emit = defineEmits<{
  open: []
  toggle: []
  'update:menuOpen': [open: boolean]
  'rename-commit': [title: string]
  'rename-cancel': []
  'menu-rename': []
  'menu-add-subpage': []
  'menu-move': []
  'menu-trash': []
}>()

const { t } = useI18n()

// Deeper levels are indented with classes that exist in the stylesheet; no style attribute is set.
const indent = [
  'ps-2',
  'ps-6',
  'ps-10',
  'ps-14',
  'ps-18',
  'ps-22',
  'ps-26',
  'ps-30',
  'ps-34',
  'ps-38',
] as const
const indentClass = () => indent[Math.min(props.row.level - 1, indent.length - 1)]

const menuItem =
  'flex min-h-11 cursor-pointer items-center rounded px-space-md text-body-sm outline-none data-[highlighted]:bg-surface-container-high md:min-h-8'

const label = () => props.row.page.title || t('tree.untitled')

const input = useTemplateRef<HTMLInputElement>('input')
const draft = ref(props.row.page.title)
let finished = false

onMounted(() => {
  if (props.renaming) focusInput()
})

function focusInput() {
  finished = false
  draft.value = props.row.page.title
  void nextTick(() => {
    input.value?.focus()
    input.value?.select()
  })
}

defineExpose({ focusInput })

function commit() {
  if (finished) return
  finished = true
  emit('rename-commit', draft.value)
}

function cancel() {
  if (finished) return
  finished = true
  emit('rename-cancel')
}
</script>

<template>
  <li
    role="treeitem"
    :aria-level="row.level"
    :aria-setsize="row.setSize"
    :aria-posinset="row.position"
    :aria-expanded="row.hasChildren ? row.expanded : undefined"
    :aria-selected="selected"
    :tabindex="tabbable ? 0 : -1"
    :draggable="draggable && !renaming"
    :data-page-id="row.page.id"
    :class="[
      'group relative flex min-h-11 cursor-pointer items-center gap-space-xs rounded pe-space-xs text-body-sm outline-none md:min-h-8',
      indentClass(),
      selected
        ? 'bg-primary-container text-on-primary-container'
        : 'text-on-surface hover:bg-surface-elevated',
      'focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary',
      dragging ? 'opacity-40' : '',
      dropZone === 'inside'
        ? 'bg-primary-container/30 outline-2 -outline-offset-2 outline-primary-container'
        : '',
      dropZone === 'before' ? 'border-t-2 border-primary' : '',
      dropZone === 'after' ? 'border-b-2 border-primary' : '',
    ]"
    @click="emit('open')"
    @dblclick.prevent="canEdit && $emit('menu-rename')"
  >
    <button
      v-if="row.hasChildren"
      type="button"
      tabindex="-1"
      :aria-label="
        row.expanded ? t('tree.collapse', { title: label() }) : t('tree.expand', { title: label() })
      "
      class="flex size-7 shrink-0 items-center justify-center rounded text-on-surface-variant hover:bg-surface-container-high md:size-6"
      @click.stop="emit('toggle')"
    >
      <AppIcon name="chevron" :class="row.expanded ? 'rotate-90' : ''" />
    </button>
    <span v-else class="size-7 shrink-0 md:size-6" aria-hidden="true" />

    <input
      v-if="renaming"
      ref="input"
      v-model="draft"
      type="text"
      maxlength="500"
      :aria-label="t('tree.renameLabel')"
      :placeholder="t('tree.untitled')"
      class="min-w-0 flex-1 rounded border border-primary-container bg-surface-canvas px-space-sm py-space-xs text-body-sm text-on-surface outline-none"
      @click.stop
      @dblclick.stop
      @keydown.enter.prevent.stop="commit"
      @keydown.esc.prevent.stop="cancel"
      @keydown.stop
      @blur="commit"
    />
    <span
      v-else
      class="min-w-0 flex-1 truncate"
      :class="row.page.title ? '' : 'text-on-surface-variant italic'"
      >{{ label() }}</span
    >

    <DropdownMenuRoot
      v-if="canEdit && !renaming"
      :open="menuOpen"
      @update:open="emit('update:menuOpen', $event)"
    >
      <DropdownMenuTrigger as-child>
        <button
          type="button"
          tabindex="-1"
          :aria-label="t('tree.actions', { title: label() })"
          :class="[
            'flex size-7 shrink-0 items-center justify-center rounded text-on-surface-variant hover:bg-surface-container-high focus-visible:opacity-100 md:size-6',
            menuOpen || selected
              ? 'opacity-100'
              : 'opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 pointer-coarse:opacity-100',
          ]"
          @click.stop
        >
          <AppIcon name="dots" />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuPortal>
        <DropdownMenuContent
          align="end"
          :side-offset="4"
          class="z-50 min-w-48 rounded-md border border-border-subtle bg-surface-elevated p-space-xs text-body-sm text-on-surface shadow-lg"
          @close-auto-focus.prevent
        >
          <DropdownMenuItem :class="menuItem" @select="emit('menu-rename')">{{
            t('tree.rename')
          }}</DropdownMenuItem>
          <DropdownMenuItem :class="menuItem" @select="emit('menu-add-subpage')">{{
            t('tree.addSubpage')
          }}</DropdownMenuItem>
          <DropdownMenuItem :class="menuItem" @select="emit('menu-move')">{{
            t('tree.moveTo')
          }}</DropdownMenuItem>
          <DropdownMenuItem :class="[menuItem, 'text-error']" @select="emit('menu-trash')">{{
            t('tree.moveToTrash')
          }}</DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenuPortal>
    </DropdownMenuRoot>
  </li>
</template>
