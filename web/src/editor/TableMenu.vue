<script setup lang="ts">
import { autoUpdate, computePosition, flip, offset, shift } from '@floating-ui/dom'
import type { Editor } from '@tiptap/core'
import { computed, nextTick, onBeforeUnmount, reactive, ref, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'

const props = defineProps<{
  editor: Editor
  /** On a phone it is a row above the toolbar; otherwise it floats above the table. */
  compact: boolean
}>()

const { t } = useI18n()
const menu = useTemplateRef<HTMLElement>('menu')
const position = reactive({ left: 0, top: 0 })
const placed = ref(false)

interface TableAction {
  id: string
  label: string
  run: () => void
  destructive?: boolean
}

/** The last row cannot be deleted on its own (ProseMirror refuses), and a table without rows is no table: it goes. */
function deleteRow() {
  const { $from } = props.editor.state.selection
  for (let depth = $from.depth; depth > 0; depth--) {
    if ($from.node(depth).type.name === 'table') {
      const only = $from.node(depth).childCount === 1
      const chain = props.editor.chain().focus()
      void (only ? chain.deleteTable() : chain.deleteRow()).run()
      return
    }
  }
}

const actions = computed<TableAction[]>(() => {
  const chain = () => props.editor.chain().focus()
  return [
    {
      id: 'addRowBefore',
      label: t('editor.table.addRowBefore'),
      run: () => void chain().addRowBefore().run(),
    },
    {
      id: 'addRowAfter',
      label: t('editor.table.addRowAfter'),
      run: () => void chain().addRowAfter().run(),
    },
    {
      id: 'addColumnBefore',
      label: t('editor.table.addColumnBefore'),
      run: () => void chain().addColumnBefore().run(),
    },
    {
      id: 'addColumnAfter',
      label: t('editor.table.addColumnAfter'),
      run: () => void chain().addColumnAfter().run(),
    },
    {
      id: 'deleteRow',
      label: t('editor.table.deleteRow'),
      run: deleteRow,
      destructive: true,
    },
    {
      id: 'deleteColumn',
      label: t('editor.table.deleteColumn'),
      run: () => void chain().deleteColumn().run(),
      destructive: true,
    },
    {
      id: 'deleteTable',
      label: t('editor.table.deleteTable'),
      run: () => void chain().deleteTable().run(),
      destructive: true,
    },
  ]
})

/** The table the caret is in. */
function tableElement(): HTMLElement | null {
  const { node } = props.editor.view.domAtPos(props.editor.state.selection.from)
  const element = node instanceof HTMLElement ? node : node.parentElement
  return element?.closest('table') ?? null
}

let stopFollowing: (() => void) | null = null
async function follow() {
  stopFollowing?.()
  stopFollowing = null
  if (props.compact) return
  await nextTick()
  const table = tableElement()
  if (!menu.value || !table) return
  const el = menu.value
  stopFollowing = autoUpdate(table, el, async () => {
    const result = await computePosition(table, el, {
      strategy: 'fixed',
      placement: 'top-start',
      middleware: [offset(4), flip(), shift({ padding: 8 })],
    })
    position.left = result.x
    position.top = result.y
    placed.value = true
  })
}

// Follows the table the caret is in, and the page as it scrolls.
watch(() => [props.compact, props.editor.state.selection.from], follow, {
  immediate: true,
  flush: 'post',
})
onBeforeUnmount(() => stopFollowing?.())
</script>

<template>
  <div
    ref="menu"
    role="toolbar"
    data-editor-chrome
    data-testid="table-menu"
    :aria-label="t('editor.table.label')"
    class="z-40 flex gap-space-xs overflow-x-auto border border-border-subtle bg-surface-subtle p-space-xs text-body-sm text-on-surface"
    :class="
      compact
        ? 'fixed inset-x-0 bottom-[calc(var(--keyboard-inset,0px)+3.5rem)] border-x-0'
        : ['fixed max-w-[calc(100vw-1rem)] rounded-lg shadow-lg', { invisible: !placed }]
    "
    :style="compact ? {} : { left: `${position.left}px`, top: `${position.top}px` }"
  >
    <!-- The pointer must not take the focus from the table, or the caret that says which row goes is lost. -->
    <button
      v-for="action in actions"
      :key="action.id"
      type="button"
      :data-testid="`table-${action.id}`"
      class="min-h-9 shrink-0 rounded-md px-space-md whitespace-nowrap outline-none hover:bg-surface-elevated focus-visible:ring-2 focus-visible:ring-primary pointer-coarse:min-h-11"
      :class="action.destructive ? 'text-error' : ''"
      @pointerdown.prevent
      @mousedown.prevent
      @click="action.run()"
    >
      {{ action.label }}
    </button>
  </div>
</template>
