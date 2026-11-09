<script setup lang="ts">
import { EditorContent, useEditor } from '@tiptap/vue-3'
import type { Editor } from '@tiptap/core'
import type { EditorView } from '@tiptap/pm/view'
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ApiError } from '../api'
import { useKeyboardInset } from '../composables/useKeyboardInset'
import { useMediaQuery } from '../composables/useMediaQuery'
import { useNotificationsStore } from '../stores/notifications'
import { useSyncStore } from '../stores/sync'
import { useTreeStore } from '../stores/tree'
import { formatFileSize } from '../fileSize'
import { uploadAttachment } from './attachmentApi'
import { editorClasses } from './editorClasses'
import { refreshBrokenLinks, type LinkTarget } from './brokenLinks'
import { openPageMenu } from './blockCommands'
import { registerLinkShortcut } from './linkShortcutRegistry'
import { selectedRange } from './selectedRange'
import { linkableRange, type TextRange } from './linkRange'
import { placeAt, type Place } from './place'
import { createPageMenuState } from './pageLinkExtension'
import { createExtensions } from './extensions'
import { applyFirstParagraph } from './firstParagraph'
import BlockHandle from './BlockHandle.vue'
import EditorToolbar from './EditorToolbar.vue'
import LinkMenu from './LinkMenu.vue'
import LinkPicker from './LinkPicker.vue'
import PageLinkMenu from './PageLinkMenu.vue'
import { sanitizePastedHtml } from './sanitize'
import SlashMenu from './SlashMenu.vue'
import { openLinkIn } from './linkOpening'
import { createSlashState } from './slashExtension'
import TableMenu from './TableMenu.vue'

const props = defineProps<{ pageId: string; readOnly: boolean }>()

const { t, locale } = useI18n()
const sync = useSyncStore()
const tree = useTreeStore()
const notifications = useNotificationsStore()

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
/** A page whose state is this large may be slow to open and to edit (run 1.16d): an imported note is where one comes from. */
const LARGE_PAGE_BYTES = 5 * 1024 * 1024
/** The size of the page as the server sent it the first time (the sync client counts it), so the document is not encoded again. */
const pageBytes = computed(() => info.value?.firstSyncBytes ?? 0)
/** The page came from a newer version of the app than this tab runs. */
const contentError = ref(false)

const editable = computed(() => {
  const page = info.value
  if (!page || props.readOnly || page.readOnly || contentError.value) return false
  if (page.state === 'missing' || page.state === 'tooLarge') return false
  return everSynced.value || sync.connection === 'offline'
})

const slashState = createSlashState()
const pageMenuState = createPageMenuState()

/** Whether the link picker is open, and whether the text it is about is a link already (it can then take it away). */
const picker = reactive({ open: false, hasLink: false })
/**
 * The text the picker is about, kept as Yjs relative positions: the picker can stay open a while, and a change from another
 * client meanwhile moves the text (it replaces the blocks it touched, so ProseMirror's own mapping would lose it).
 */
let pickerText: { from: Place; to: Place } | null = null

/** What the picker would be about now (`linkableRange`), or null when there is nothing to link or the page cannot be edited. */
function linkPickerRange(): TextRange | null {
  const current = editor.value
  if (!current || !editable.value) return null
  return linkableRange(current.state, selectedRange(current.view))
}

/**
 * Opens the picker for the selected text, or for the whole link when the caret or the selection is in one (to change it).
 * False when there is nothing to link.
 */
function openLinkPicker(): boolean {
  const current = editor.value
  const range = linkPickerRange()
  if (!current || !range) return false
  pickerText = { from: placeAt(current, range.from), to: placeAt(current, range.to) }
  picker.hasLink = current.state.doc.rangeHasMark(range.from, range.to, current.schema.marks.link!)
  picker.open = true
  return true
}

/** Where the picker's text is now; null when it was deleted meanwhile, or the page can no longer be edited. */
function pickerRange(): TextRange | null {
  const from = pickerText?.from.resolve()
  const to = pickerText?.to.resolve()
  if (from == null || to == null || from >= to || !editable.value) return null
  return { from, to }
}

/**
 * Back to the text: with what was selected selected again, or the caret after the link that was made. The focus goes back to
 * the editor also when the text is gone.
 */
function closePicker(caretAfter: boolean) {
  const range = pickerRange()
  picker.open = false
  pickerText = null
  const current = editor.value
  if (!current || current.isDestroyed) return
  const chain = current.chain().focus()
  // After a link that was made or changed, what is typed next is not part of it (see `insertPageLink`).
  if (range && caretAfter) chain.setTextSelection(range.to).unsetMark('link')
  else if (range) chain.setTextSelection(range)
  chain.run()
}

/** Changes the link with `change`, on the picker's text where it is now; nothing when it is gone or the page became read-only. */
function changePickerText(change: (chain: ReturnType<Editor['chain']>) => void) {
  const current = editor.value
  const range = pickerRange()
  if (current && range) {
    const chain = current.chain().focus().setTextSelection(range)
    change(chain)
    chain.run()
  }
  closePicker(true)
}

const chooseLink = (href: string) => changePickerText((chain) => chain.setLink({ href }))
const removeLink = () => changePickerText((chain) => chain.unsetLink())

// A page that stops being editable while the picker is open (it went to the trash, the role changed) gets no link.
watch(editable, (can) => {
  if (!can && picker.open) closePicker(false)
})

/** The link button of the toolbar: the picker for selected text, otherwise `[[`, which opens the menu of pages. */
function toolbarLink() {
  const current = editor.value
  if (!current || !editable.value) return
  if (!openLinkPicker()) openPageMenu(current)
}

// Ctrl+K: the editor takes it, and not search, when there is text to link (the app asks before it opens search). The same
// question as the one the picker asks when it opens, so the two never disagree: if they did, neither would act on the key.
const stopLinkShortcut = registerLinkShortcut(
  () => !!editor.value?.view.hasFocus() && linkPickerRange() !== null,
)

// The drag handle: the extension moves this element next to the block under the mouse, and BlockHandle fills it. Only
// with a mouse; on a touch screen the toolbar and the slash menu do this.
const handleElement = document.createElement('div')
handleElement.className =
  'hidden opacity-20 transition-opacity hover:opacity-100 focus-within:opacity-100 pointer-fine:block'
let hoveredBlock = -1

// The toolbar is the phone's way to undo, redo and insert blocks: on a touch screen or a narrow window, while the
// editor (or the toolbar itself, reached with the keyboard) has the focus.
const TOOLBAR_HEIGHT = 56
const touch = useMediaQuery('(pointer: coarse)')
const wide = useMediaQuery('(min-width: 48rem)', true)
const toolbarWanted = computed(() => (touch.value || !wide.value) && editable.value)
const focusWithin = ref(false)
const showToolbar = computed(() => toolbarWanted.value && focusWithin.value)
useKeyboardInset(showToolbar)

/**
 * Focus moves from the editor to the toolbar through a moment where neither has it, so a loss is only believed once
 * the focus has settled; otherwise the toolbar would go away under the finger that reaches for it.
 */
function refreshFocus() {
  // A blur that came just before the person left the page is looked at after the editor is gone: nothing to ask then.
  if (editor.value?.isDestroyed) return
  const active = document.activeElement
  focusWithin.value =
    !!active &&
    (editor.value?.view.dom.contains(active) === true ||
      active.closest('[data-editor-chrome]') !== null)
}
function focusChanged() {
  setTimeout(refreshFocus, 0)
}

// Alt+Arrow moves a block with no sign on the screen that a screen reader could follow, so it is said.
const announcement = ref('')
async function announceMove(direction: -1 | 1) {
  announcement.value = ''
  await nextTick()
  announcement.value = direction < 0 ? t('editor.movedUp') : t('editor.movedDown')
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

/** Files on their way to the server; they go into the page once they are there. */
const uploading = ref(0)
const router = useRouter()
const openLink = openLinkIn(router)

/**
 * What a page that a link points to is now, by the tree this tab has: there, in the trash, or not to be found. Until the tree
 * is read nothing is known, so nothing is marked rather than every link for a moment.
 */
const trashed = computed(() => new Set(tree.trash.map((page) => page.id)))
function linkTarget(pageId: string): LinkTarget {
  if (!tree.loaded || tree.byId.has(pageId)) return 'live'
  return trashed.value.has(pageId) ? 'trash' : 'unavailable'
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
    pageLinks: {
      state: pageMenuState,
      pages: () => tree.pages,
      currentPageId: () => props.pageId,
      untitled: () => t('tree.untitled'),
    },
    linkShortcut: { open: openLinkPicker },
    // A page with something in it that this schema does not know (made by a newer version of the app, with a block this
    // tab has never heard of) cannot be shown. y-tiptap keeps it and stops writing the editor's changes to the page, so
    // nothing is deleted. TipTap's own `contentError` covers the other way in: a change from another client that makes
    // the document invalid.
    onInvalidContent: () => (contentError.value = true),
    openLink,
    brokenLinks: {
      stateOf: linkTarget,
      describe: (state) =>
        state === 'trash' ? t('editor.link.inTrash') : t('editor.link.unavailable'),
    },
    onBlockMoved: announceMove,
    dragHandle: {
      element: handleElement, // Leaving the block for the handle reports no block; the handle still belongs to the one it was next to.
      onHover: (pos) => {
        if (pos >= 0) hoveredBlock = pos
      },
    },
    files: {
      formatSize: (bytes) => formatFileSize(bytes, locale.value),
      downloadLabel: (fileName, size) => t('editor.attachment.download', { name: fileName, size }),
    },
    upload: {
      upload: (file) => uploadAttachment(props.pageId, file),
      onError: (error) =>
        notifications.post({
          type: 'error',
          code: error instanceof ApiError ? error.code : 'unknown',
        }),
      onBusyChange: (count) => (uploading.value = count),
    },
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
  editorProps: {
    attributes: attributes(),
    transformPastedHTML: sanitizePastedHtml,
    handleScrollToSelection: keepCaretAboveToolbar,
  },
})

const inTable = computed(
  () => editable.value && focusWithin.value && editor.value?.isActive('table') === true,
)

const inLink = computed(
  () => editable.value && focusWithin.value && editor.value?.isActive('link') === true,
)

/**
 * A request for the focus (Enter in the title) that came before the page could be typed into: a page that is still being
 * read from the server is not editable, and the request would be dropped, with the person's next keystrokes going nowhere.
 * It is kept for a moment and carried out the moment the editor can be typed into. Anything the person does meanwhile (a
 * key, a click or a tap anywhere) means they went on without the editor, and the request is dropped: the focus is never
 * taken from where they are typing. After a few seconds it is dropped too.
 */
const FOCUS_REQUEST_LIFETIME_MS = 5000
let focusRequestedAt = 0

function dropFocusRequest() {
  focusRequestedAt = 0
  document.removeEventListener('keydown', dropFocusRequest, true)
  document.removeEventListener('pointerdown', dropFocusRequest, true)
}

function focusAtStart() {
  dropFocusRequest()
  if (editable.value) {
    editor.value?.commands.focus('start')
    return
  }
  focusRequestedAt = Date.now()
  // Added while the key that asked (Enter) is being handled, so that key itself does not drop it.
  document.addEventListener('keydown', dropFocusRequest, true)
  document.addEventListener('pointerdown', dropFocusRequest, true)
}

watch(
  [editor, editable],
  ([current, can]) => {
    current?.setEditable(can, false)
    handleElement.toggleAttribute('hidden', !can)
    if (can && focusRequestedAt > 0) {
      const fresh = Date.now() - focusRequestedAt < FOCUS_REQUEST_LIFETIME_MS
      dropFocusRequest()
      if (fresh) current?.commands.focus('start')
    }
  },
  { immediate: true },
)
watch(locale, () => editor.value?.setOptions({ editorProps: { attributes: attributes() } }))

// A link to a page that goes to the trash, comes back or is made is marked by the tree, not by the document: when the tree
// changes, the links are looked at again.
watch([() => tree.pages, () => tree.trash, () => tree.loaded], () => {
  if (editor.value) refreshBrokenLinks(editor.value.view)
})

onBeforeUnmount(() => {
  stopLinkShortcut()
  dropFocusRequest()
  editor.value?.destroy()
  sync.close(props.pageId)
})

defineExpose({ focus: focusAtStart })
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
    <p
      v-if="pageBytes > LARGE_PAGE_BYTES"
      role="status"
      data-testid="large-page"
      class="rounded border border-border-subtle bg-surface-subtle p-space-md text-body-sm text-on-surface-variant"
    >
      {{ t('editor.largePage', { size: formatFileSize(pageBytes, locale) }) }}
    </p>
    <EditorContent :editor="editor" data-testid="editor" />
    <BlockHandle
      v-if="editor"
      :editor="editor"
      :element="handleElement"
      :hovered="() => hoveredBlock"
    />
    <p aria-live="polite" aria-atomic="true" class="sr-only" data-testid="editor-announcer">
      {{ announcement }}
    </p>
    <p
      v-if="uploading > 0"
      role="status"
      data-testid="attachment-uploading"
      class="text-body-sm text-on-surface-variant"
    >
      {{ t('editor.attachment.uploading', { count: uploading }, uploading) }}
    </p>
    <SlashMenu :state="slashState" :reserve-bottom="showToolbar ? TOOLBAR_HEIGHT : 0" />
    <PageLinkMenu :state="pageMenuState" :reserve-bottom="showToolbar ? TOOLBAR_HEIGHT : 0" />
    <LinkPicker
      :open="picker.open"
      :page-id="pageId"
      :has-link="picker.hasLink"
      @choose="chooseLink"
      @remove="removeLink"
      @cancel="closePicker(false)"
    />
    <LinkMenu
      v-if="editor && inLink"
      :editor="editor"
      :open="openLink"
      @change="openLinkPicker"
      @focusin="refreshFocus"
      @focusout="focusChanged"
    />
    <TableMenu
      v-if="editor && inTable"
      :editor="editor"
      :compact="showToolbar"
      @focusin="refreshFocus"
      @focusout="focusChanged"
    />
    <EditorToolbar
      v-if="editor && showToolbar"
      :editor="editor"
      @link="toolbarLink"
      @focusin="refreshFocus"
      @focusout="focusChanged"
    />
  </div>
</template>
