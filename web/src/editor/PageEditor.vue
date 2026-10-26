<script setup lang="ts">
import { ySyncPluginKey } from '@tiptap/y-tiptap'
import { EditorContent, useEditor } from '@tiptap/vue-3'
import type { Editor } from '@tiptap/core'
import type { EditorView } from '@tiptap/pm/view'
import { computed, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useKeyboardInset } from '../composables/useKeyboardInset'
import { useMediaQuery } from '../composables/useMediaQuery'
import { useSyncStore } from '../stores/sync'
import { editorClasses } from './editorClasses'
import { createExtensions } from './extensions'
import { applyFirstParagraph } from './firstParagraph'
import EditorToolbar from './EditorToolbar.vue'
import { sanitizePastedHtml } from './sanitize'
import SlashMenu from './SlashMenu.vue'
import { createSlashState } from './slashExtension'

const props = defineProps<{ pageId: string; readOnly: boolean }>()

const { t, locale } = useI18n()
const sync = useSyncStore()

// The sync client makes the document, so that it can keep it alive while changes are still on their way after the
// person left. The first paragraph is applied once, before the subscribe.
const handle = sync.open(props.pageId, { readOnly: props.readOnly, init: applyFirstParagraph })
const info = computed(() => sync.pages[props.pageId])

// A page that has not been in step with the server yet may be empty only because its content has not arrived: typing
// then would land in the first paragraph. Once it has been in step, or when there is plainly no server, typing is fine.
const everSynced = ref(info.value?.state === 'synced')
watch(
  () => info.value?.state,
  (state) => {
    if (state === 'synced') everSynced.value = true
  },
)
/** The page came from a newer version of the app than this tab runs. */
const contentError = ref(false)

const editable = computed(() => {
  const page = info.value
  if (!page || props.readOnly || page.readOnly || contentError.value) return false
  if (page.state === 'missing' || page.state === 'tooLarge') return false
  return everSynced.value || sync.connection === 'offline'
})

/**
 * A page with something in it that this schema does not know (made by a newer version of the app, with a block this tab
 * has never heard of) cannot be shown. y-tiptap then stops writing the editor's changes to the page, so nothing is
 * deleted, and says so in its binding. TipTap's own `contentError` covers the other way in: a change from another client
 * that makes the document invalid.
 */
function checkProjection(current: Editor) {
  if (ySyncPluginKey.getState(current.state)?.binding?.projectionError) contentError.value = true
}

const slashState = createSlashState()

// The toolbar is the phone's way to undo, redo and insert blocks: on a touch screen or a narrow window, while the
// editor (or the toolbar itself, reached with the keyboard) has the focus.
const TOOLBAR_HEIGHT = 56
const touch = useMediaQuery('(pointer: coarse)')
const wide = useMediaQuery('(min-width: 48rem)', true)
const toolbarWanted = computed(() => (touch.value || !wide.value) && editable.value)
const focusWithin = ref(false)
const toolbar = useTemplateRef<InstanceType<typeof EditorToolbar>>('toolbar')
const showToolbar = computed(() => toolbarWanted.value && focusWithin.value)
useKeyboardInset(showToolbar)

/**
 * Focus moves from the editor to the toolbar through a moment where neither has it, so a loss is only believed once
 * the focus has settled; otherwise the toolbar would go away under the finger that reaches for it.
 */
function refreshFocus() {
  const active = document.activeElement
  focusWithin.value =
    !!active &&
    (editor.value?.view.dom.contains(active) === true ||
      toolbar.value?.$el?.contains(active) === true)
}
function focusChanged() {
  setTimeout(refreshFocus, 0)
}

/**
 * Keeps the caret above the toolbar, and so above the keyboard, when ProseMirror scrolls it into view. The browser does
 * not know about the toolbar, and ProseMirror's own margin did not move the page's scroller. Returns false when
 * nothing needs to be done, which leaves it to ProseMirror.
 */
function keepCaretAboveToolbar(view: EditorView): boolean {
  if (!showToolbar.value) return false
  const caret = view.coordsAtPos(view.state.selection.head)
  const screen = window.visualViewport
  const limit =
    (screen ? screen.offsetTop + screen.height : window.innerHeight) - TOOLBAR_HEIGHT - 8
  const hidden = caret.bottom - limit
  if (hidden <= 0) return false
  let scroller: HTMLElement | null = view.dom.parentElement
  while (scroller && !/(auto|scroll)/.test(getComputedStyle(scroller).overflowY)) {
    scroller = scroller.parentElement
  }
  if (!scroller) return false
  scroller.scrollTop += hidden
  return true
}

function attributes() {
  return {
    role: 'textbox',
    'aria-multiline': 'true',
    'aria-label': t('editor.label'),
    class: editorClasses,
  }
}

const editor = useEditor({
  extensions: createExtensions({
    doc: handle.doc,
    placeholder: t('editor.placeholder'),
    // Written out, so the lint rule that finds unused translations can see the keys.
    checkboxLabel: (checked) => (checked ? t('editor.checkboxDone') : t('editor.checkboxNotDone')),
    slash: { state: slashState, translate: (key) => t(key) },
  }),
  editable: editable.value,
  // TipTap would add its stylesheet as a <style> element, which the strict policy blocks (see style.css).
  injectCSS: false,
  // A tab that runs an older bundle drops nodes it does not know, and its next edit would delete them from the page for
  // everybody. With the check it stops syncing this page instead.
  enableContentCheck: true,
  onContentError: ({ disableCollaboration }) => {
    disableCollaboration()
    contentError.value = true
  },
  onFocus: refreshFocus,
  onBlur: focusChanged,
  onCreate: ({ editor: created }) => checkProjection(created),
  onTransaction: ({ editor: changed }) => checkProjection(changed),
  editorProps: {
    attributes: attributes(),
    transformPastedHTML: sanitizePastedHtml,
    handleScrollToSelection: keepCaretAboveToolbar,
  },
})

watch([editor, editable], ([current, can]) => current?.setEditable(can, false), { immediate: true })
watch(locale, () => editor.value?.setOptions({ editorProps: { attributes: attributes() } }))

onBeforeUnmount(() => {
  editor.value?.destroy()
  sync.close(props.pageId)
})

defineExpose({ focus: () => editor.value?.commands.focus('start') })
</script>

<template>
  <div
    class="flex flex-col gap-space-md"
    :class="showToolbar ? 'pb-[calc(3.5rem+var(--keyboard-inset,0px))]' : ''"
  >
    <p
      v-if="contentError"
      role="alert"
      class="rounded border border-error p-space-md text-body-sm text-error"
    >
      {{ t('editor.newerVersion') }}
    </p>
    <EditorContent :editor="editor" data-testid="editor" />
    <SlashMenu :state="slashState" :reserve-bottom="showToolbar ? TOOLBAR_HEIGHT : 0" />
    <EditorToolbar
      v-if="editor && showToolbar"
      ref="toolbar"
      :editor="editor"
      @focusin="refreshFocus"
      @focusout="focusChanged"
    />
  </div>
</template>
