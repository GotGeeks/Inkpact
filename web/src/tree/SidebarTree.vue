<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useTemplateRef } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import { useMediaQuery } from '../composables/useMediaQuery'
import { useTreeStore } from '../stores/tree'
import MovePageDialog from './MovePageDialog.vue'
import TreeRow from './TreeRow.vue'
import {
  dropPosition,
  keyboardMove,
  visibleRows,
  zoneAt,
  type DropZone,
  type MoveDirection,
  type Row,
} from './treeModel'

const props = defineProps<{ selectedId: string | null }>()
const emit = defineEmits<{
  open: [id: string]
  /** A page was just made. The caller shows it, but keeps the sidebar open, because its title is about to be typed. */
  created: [id: string]
}>()

const { t } = useI18n()
const tree = useTreeStore()

const rows = computed(() => visibleRows(tree.pages, tree.expanded))

// Only a mouse or a pen drags. A finger would scroll, and phones have "Move to…", which is easier to use anyway.
const canDrag = useMediaQuery('(pointer: fine)')

// ---- focus: one row at a time can be reached with Tab; the arrow keys move between them -----------------------------

const focusedId = ref<string | null>(null)
const tabbableId = computed(() => {
  const ids = rows.value.map((row) => row.page.id)
  if (focusedId.value && ids.includes(focusedId.value)) return focusedId.value
  if (props.selectedId && ids.includes(props.selectedId)) return props.selectedId
  return ids[0] ?? null
})

const list = useTemplateRef<HTMLElement>('list')

function focusRow(id: string) {
  focusedId.value = id
  void nextTick(() =>
    [...(list.value?.querySelectorAll<HTMLElement>('[data-page-id]') ?? [])]
      .find((element) => element.dataset.pageId === id)
      ?.focus(),
  )
}

// ---- rename ------------------------------------------------------------------------------------------------------------

const renamingId = ref<string | null>(null)
const rowRefs = ref<Record<string, InstanceType<typeof TreeRow> | null>>({})

function startRename(id: string) {
  if (!tree.canEdit) return
  renamingId.value = id
  void nextTick(() => rowRefs.value[id]?.focusInput())
}

async function commitRename(id: string, title: string) {
  renamingId.value = null
  if (title.trim() !== (tree.byId.get(id)?.title ?? '')) await tree.rename(id, title)
  focusRow(id)
}

function cancelRename(id: string) {
  renamingId.value = null
  focusRow(id)
}

// ---- creating, moving, trashing ----------------------------------------------------------------------------------------

async function newPage(parentId: string | null = null) {
  const change = await tree.create('', { parentId })
  if (!change) return
  emit('created', change.page.id)
  await nextTick()
  startRename(change.page.id)
}

async function moveWith(id: string, direction: MoveDirection) {
  const position = keyboardMove(tree.pages, id, direction)
  if (position && (await tree.move(id, position))) focusRow(id)
}

async function trashRow(id: string) {
  // Focus goes to the row below, or above when it was the last, so that the keyboard does not lose its place.
  const ids = rows.value.map((row) => row.page.id)
  const index = ids.indexOf(id)
  const next = ids[index + 1] ?? ids[index - 1] ?? null
  if (await tree.trashPage(id)) {
    if (next) focusRow(next)
  }
}

const movingId = ref<string | null>(null)

// ---- the menu of a row -------------------------------------------------------------------------------------------------

const menuOpenId = ref<string | null>(null)

// ---- keyboard ----------------------------------------------------------------------------------------------------------

function onKeydown(event: KeyboardEvent, row: Row, index: number) {
  // Keys typed in the title field or pressed on a button inside the row are theirs.
  if (event.target !== event.currentTarget) return
  const id = row.page.id
  const all = rows.value

  if (event.altKey && !event.ctrlKey && !event.metaKey) {
    const direction: Partial<Record<string, MoveDirection>> = {
      ArrowUp: 'up',
      ArrowDown: 'down',
      ArrowRight: 'indent',
      ArrowLeft: 'outdent',
    }
    const move = direction[event.key]
    if (move && tree.canEdit) {
      event.preventDefault()
      void moveWith(id, move)
    }
    return
  }
  if (event.ctrlKey || event.metaKey) return

  switch (event.key) {
    case 'ArrowDown':
      event.preventDefault()
      if (all[index + 1]) focusRow(all[index + 1]!.page.id)
      break
    case 'ArrowUp':
      event.preventDefault()
      if (all[index - 1]) focusRow(all[index - 1]!.page.id)
      break
    case 'Home':
      event.preventDefault()
      focusRow(all[0]!.page.id)
      break
    case 'End':
      event.preventDefault()
      focusRow(all.at(-1)!.page.id)
      break
    case 'ArrowRight':
      event.preventDefault()
      if (row.hasChildren && !row.expanded) tree.setExpanded(id, true)
      else if (row.hasChildren) focusRow(all[index + 1]!.page.id)
      break
    case 'ArrowLeft':
      event.preventDefault()
      if (row.expanded) tree.setExpanded(id, false)
      else if (row.page.parentId) focusRow(row.page.parentId)
      break
    case 'Enter':
    case ' ':
      event.preventDefault()
      emit('open', id)
      break
    case 'F2':
      event.preventDefault()
      startRename(id)
      break
    case 'Delete':
      if (tree.canEdit) {
        event.preventDefault()
        void trashRow(id)
      }
      break
    case 'ContextMenu':
      event.preventDefault()
      if (tree.canEdit) menuOpenId.value = id
      break
    case 'F10':
      if (event.shiftKey && tree.canEdit) {
        event.preventDefault()
        menuOpenId.value = id
      }
      break
  }
}

// ---- dragging with a mouse ---------------------------------------------------------------------------------------------

const draggingId = ref<string | null>(null)
const hint = ref<{ id: string; zone: DropZone } | null>(null)
let openTimer: ReturnType<typeof setTimeout> | undefined

function clearDrag() {
  draggingId.value = null
  hint.value = null
  clearTimeout(openTimer)
}

onBeforeUnmount(() => clearTimeout(openTimer))

function onDragStart(event: DragEvent, row: Row) {
  draggingId.value = row.page.id
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'move'
    // Some browsers do not start a drag without data. The page id is all there is to carry.
    event.dataTransfer.setData('text/plain', row.page.id)
  }
}

function onDragOver(event: DragEvent, row: Row) {
  const dragged = draggingId.value
  if (!dragged) return
  const element = event.currentTarget as HTMLElement
  const zone = zoneAt(
    event.clientY - element.getBoundingClientRect().top,
    element.getBoundingClientRect().height,
  )
  if (!dropPosition(tree.pages, dragged, row.page.id, zone)) {
    hint.value = null
    return
  }

  event.preventDefault() // allows the drop
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
  if (hint.value?.id !== row.page.id || hint.value.zone !== zone) {
    hint.value = { id: row.page.id, zone }
    clearTimeout(openTimer)
    // Held over a closed branch for a moment, it opens, so that a page can be dropped deep inside.
    if (zone === 'inside' && row.hasChildren && !row.expanded) {
      openTimer = setTimeout(() => tree.setExpanded(row.page.id, true), 700)
    }
  }
}

async function onDrop(event: DragEvent, row: Row) {
  const dragged = draggingId.value
  const zone = hint.value?.id === row.page.id ? hint.value.zone : null
  clearDrag()
  if (!dragged || !zone) return
  const position = dropPosition(tree.pages, dragged, row.page.id, zone)
  if (!position) return
  event.preventDefault()
  if (await tree.move(dragged, position)) focusRow(dragged)
}

defineExpose({ newPage })
</script>

<template>
  <div class="flex min-h-0 flex-1 flex-col gap-space-sm">
    <div class="flex items-center justify-between gap-space-sm px-space-sm">
      <h2 id="pages-label" class="text-label-xs font-semibold text-on-surface-variant uppercase">
        {{ t('tree.label') }}
      </h2>
      <button
        v-if="tree.canEdit"
        type="button"
        class="flex min-h-11 items-center gap-space-xs rounded border border-border-subtle px-space-md text-label-md text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-container md:min-h-8"
        @click="newPage(null)"
      >
        <AppIcon name="plus" />
        {{ t('tree.newPage') }}
      </button>
    </div>

    <p v-if="rows.length === 0" class="px-space-md text-body-sm text-on-surface-variant">
      {{ t('tree.empty') }}
    </p>

    <ul
      v-else
      ref="list"
      role="tree"
      aria-labelledby="pages-label"
      aria-multiselectable="false"
      class="min-h-0 flex-1 overflow-y-auto px-space-xs"
      @dragend="clearDrag"
    >
      <TreeRow
        v-for="(row, index) in rows"
        :key="row.page.id"
        :ref="
          (component) => (rowRefs[row.page.id] = component as InstanceType<typeof TreeRow> | null)
        "
        :row="row"
        :selected="row.page.id === selectedId"
        :tabbable="row.page.id === tabbableId"
        :renaming="renamingId === row.page.id"
        :menu-open="menuOpenId === row.page.id"
        :can-edit="tree.canEdit"
        :drop-zone="hint?.id === row.page.id ? hint.zone : null"
        :dragging="draggingId === row.page.id"
        :draggable="canDrag && tree.canEdit"
        @focusin="focusedId = row.page.id"
        @keydown="onKeydown($event, row, index)"
        @dragstart="onDragStart($event, row)"
        @dragover="onDragOver($event, row)"
        @drop="onDrop($event, row)"
        @open="emit('open', row.page.id)"
        @toggle="tree.setExpanded(row.page.id, !row.expanded)"
        @update:menu-open="menuOpenId = $event ? row.page.id : null"
        @rename-commit="commitRename(row.page.id, $event)"
        @rename-cancel="cancelRename(row.page.id)"
        @menu-rename="startRename(row.page.id)"
        @menu-add-subpage="newPage(row.page.id)"
        @menu-move="movingId = row.page.id"
        @menu-trash="trashRow(row.page.id)"
      />
    </ul>

    <MovePageDialog :page-id="movingId" @close="movingId = null" />
  </div>
</template>
