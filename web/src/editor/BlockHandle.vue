<script setup lang="ts">
import type { Editor } from '@tiptap/core'
import {
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuPortal,
  DropdownMenuRoot,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from 'reka-ui'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import {
  deleteBlock,
  duplicateBlock,
  moveBlock,
  selectBlock,
  topLevelBlockAt,
  type BlockRange,
} from './blockActions'
import { blockCommands, type BlockCommand } from './blockCommands'

const props = defineProps<{
  editor: Editor
  /** The element the drag handle extension moves along the page; this fills it. */
  element: HTMLElement
  /** The position of the top-level block the mouse is over, as the extension reports it. */
  hovered: () => number
}>()

const { t } = useI18n()
const open = ref(false)
/** The block the open menu works on, kept from when it was opened: the mouse moves on while it is open. */
let target: BlockRange | null = null

function toggle() {
  if (!open.value) target = topLevelBlockAt(props.editor.state.doc, props.hovered())
  open.value = !open.value && target !== null
}

/** A block may have changed under an open menu; act on what is at the same place now. */
function current(): BlockRange | null {
  return target ? topLevelBlockAt(props.editor.state.doc, target.from) : null
}

function run(action: (block: BlockRange) => void) {
  const block = current()
  if (block) action(block)
  props.editor.commands.focus()
}

function turnInto(command: BlockCommand) {
  run((block) => {
    selectBlock(props.editor, block)
    command.run(props.editor)
  })
}

const item =
  'flex min-h-9 cursor-pointer items-center rounded-sm px-space-md text-body-sm outline-none data-[highlighted]:bg-surface-container-high data-[highlighted]:text-primary'
const panel =
  'z-50 min-w-48 rounded-md border border-border-subtle bg-surface-elevated p-space-xs text-on-surface shadow-lg'
</script>

<template>
  <Teleport :to="element">
    <DropdownMenuRoot
      :open="open"
      :modal="false"
      @update:open="(value) => !value && (open = false)"
    >
      <!-- Reka opens on the press, which would open it at the start of every drag: only the click opens it here. -->
      <DropdownMenuTrigger as-child>
        <div
          role="button"
          tabindex="-1"
          data-testid="block-handle"
          :aria-label="t('editor.handle.label')"
          class="flex size-6 cursor-grab items-center justify-center rounded-sm text-on-surface-variant hover:bg-surface-container-high active:cursor-grabbing"
          @click="toggle"
          @keydown.enter.prevent="toggle"
          @keydown.space.prevent="toggle"
        >
          <AppIcon name="grip" class="size-4" />
        </div>
      </DropdownMenuTrigger>
      <DropdownMenuPortal>
        <DropdownMenuContent
          side="bottom"
          align="start"
          :side-offset="4"
          :class="panel"
          data-testid="block-menu"
          @close-auto-focus.prevent
        >
          <DropdownMenuSub>
            <DropdownMenuSubTrigger :class="[item, 'justify-between gap-space-md']">
              {{ t('editor.handle.turnInto') }}
              <AppIcon name="chevron" />
            </DropdownMenuSubTrigger>
            <DropdownMenuPortal>
              <DropdownMenuSubContent :class="panel" :side-offset="4">
                <DropdownMenuItem
                  v-for="command in blockCommands"
                  :key="command.id"
                  :class="[item, 'gap-space-md']"
                  @select="turnInto(command)"
                >
                  <AppIcon :name="command.icon" />
                  {{ command.label(t) }}
                </DropdownMenuItem>
              </DropdownMenuSubContent>
            </DropdownMenuPortal>
          </DropdownMenuSub>
          <DropdownMenuItem :class="item" @select="run((block) => duplicateBlock(editor, block))">
            {{ t('editor.handle.duplicate') }}
          </DropdownMenuItem>
          <DropdownMenuItem :class="item" @select="run((block) => moveBlock(editor, -1, block))">
            {{ t('editor.handle.moveUp') }}
          </DropdownMenuItem>
          <DropdownMenuItem :class="item" @select="run((block) => moveBlock(editor, 1, block))">
            {{ t('editor.handle.moveDown') }}
          </DropdownMenuItem>
          <DropdownMenuItem
            :class="[item, 'text-error']"
            @select="run((block) => deleteBlock(editor, block))"
          >
            {{ t('editor.handle.delete') }}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenuPortal>
    </DropdownMenuRoot>
  </Teleport>
</template>
