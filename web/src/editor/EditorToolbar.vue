<script setup lang="ts">
import type { Editor } from '@tiptap/core'
import { computed, nextTick, ref, useTemplateRef } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import { blockCommands } from './blockCommands'

const props = defineProps<{ editor: Editor }>()

const { t } = useI18n()
const root = useTemplateRef<HTMLElement>('root')

function command(id: string) {
  return blockCommands.find((candidate) => candidate.id === id)!
}

/** `/` only opens the slash menu at the start of a line or after a space, so one is added when the caret follows text. */
function openSlashMenu() {
  const { $from } = props.editor.state.selection
  const before = $from.parent.textBetween(Math.max(0, $from.parentOffset - 1), $from.parentOffset)
  props.editor
    .chain()
    .focus()
    .insertContent(before && before !== ' ' ? ' /' : '/')
    .run()
}

interface ToolbarButton {
  id: string
  icon: InstanceType<typeof AppIcon>['$props']['name']
  label: string
  /** Set for the buttons that switch something on and off. */
  pressed?: boolean
  disabled?: boolean
  run: () => void
}

// Every button keeps the editor's focus (see the template), so these run on the editor as it is.
const buttons = computed<ToolbarButton[]>(() => {
  const editor = props.editor
  const chain = () => editor.chain().focus()
  return [
    {
      id: 'undo',
      icon: 'undo',
      label: t('editor.toolbar.undo'),
      disabled: !editor.can().undo(),
      run: () => void chain().undo().run(),
    },
    {
      id: 'redo',
      icon: 'redo',
      label: t('editor.toolbar.redo'),
      disabled: !editor.can().redo(),
      run: () => void chain().redo().run(),
    },
    {
      id: 'heading',
      icon: 'heading',
      label: t('editor.toolbar.heading'),
      pressed: editor.isActive('heading'),
      run: () => void chain().toggleHeading({ level: 2 }).run(),
    },
    {
      id: 'bold',
      icon: 'bold',
      label: t('editor.toolbar.bold'),
      pressed: editor.isActive('bold'),
      run: () => void chain().toggleBold().run(),
    },
    {
      id: 'italic',
      icon: 'italic',
      label: t('editor.toolbar.italic'),
      pressed: editor.isActive('italic'),
      run: () => void chain().toggleItalic().run(),
    },
    {
      id: 'bulletList',
      icon: 'bulletList',
      label: command('bulletList').label(t),
      pressed: editor.isActive('bulletList'),
      run: () => command('bulletList').run(editor),
    },
    {
      id: 'checklist',
      icon: 'checklist',
      label: command('checklist').label(t),
      pressed: editor.isActive('taskList'),
      run: () => command('checklist').run(editor),
    },
    {
      id: 'code',
      icon: 'code',
      label: t('editor.toolbar.code'),
      pressed: editor.isActive('code'),
      run: () => void chain().toggleCode().run(),
    },
    { id: 'insert', icon: 'slash', label: t('editor.toolbar.insert'), run: openSlashMenu },
  ]
})

// One tab stop for the whole bar; the arrow keys move within it.
const current = ref(0)
function press(button: ToolbarButton) {
  if (!button.disabled) button.run()
}
async function move(to: number) {
  const count = buttons.value.length
  current.value = (to + count) % count
  await nextTick()
  root.value?.querySelectorAll<HTMLElement>('button')[current.value]?.focus()
}
function onKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowRight') move(current.value + 1)
  else if (event.key === 'ArrowLeft') move(current.value - 1)
  else if (event.key === 'Home') move(0)
  else if (event.key === 'End') move(buttons.value.length - 1)
  else return
  event.preventDefault()
}
</script>

<template>
  <!-- Above the keyboard: the page tells how far it covers the screen (useKeyboardInset). -->
  <div
    ref="root"
    role="toolbar"
    data-testid="editor-toolbar"
    :aria-label="t('editor.toolbar.label')"
    class="fixed inset-x-0 bottom-[var(--keyboard-inset,0px)] z-40 flex h-14 items-center gap-space-xs overflow-x-auto border-t border-border-subtle bg-surface-panel px-space-sm text-on-surface"
    @keydown="onKeydown"
  >
    <!-- The mouse must not take the focus from the editor, or the keyboard closes with every tap. Only mousedown is
         cancelled: cancelling pointerdown too stops iOS Safari from sending the click at all. -->
    <button
      v-for="(button, index) in buttons"
      :key="button.id"
      type="button"
      :data-testid="`toolbar-${button.id}`"
      :aria-label="button.label"
      :aria-pressed="button.pressed"
      :aria-disabled="button.disabled || undefined"
      :tabindex="index === current ? 0 : -1"
      class="flex size-11 shrink-0 items-center justify-center rounded-md outline-none focus-visible:ring-2 focus-visible:ring-primary"
      :class="[
        button.pressed ? 'bg-surface-elevated text-primary' : 'text-on-surface',
        button.disabled ? 'opacity-40' : '',
      ]"
      @mousedown.prevent
      @focus="current = index"
      @click="press(button)"
    >
      <AppIcon :name="button.icon" class="size-5" />
    </button>
  </div>
</template>
