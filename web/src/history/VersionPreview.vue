<script setup lang="ts">
import type { JSONContent } from '@tiptap/core'
import { EditorContent, useEditor } from '@tiptap/vue-3'
import { onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatFileSize } from '../fileSize'
import { editorClasses } from '../editor/editorClasses'
import { createExtensions } from '../editor/extensions'
import { useTreeStore } from '../stores/tree'
import type { LinkTarget } from '../editor/brokenLinks'

/**
 * A version's content, read-only, in an editor of its own built from the page editor's extensions: the same schema, the
 * same rules for links (an address is checked when it is rendered) and for files (by id, from the app's own address). It
 * has no Yjs document and no socket, so nothing it does reaches the page that is open.
 */
const props = defineProps<{
  content: JSONContent
  /** What a screen reader calls the content. */
  label: string
}>()

const { t, locale } = useI18n()
const tree = useTreeStore()

/** The content does not fit this app's schema (a newer app made it): it is not shown in part. */
const unreadable = ref(false)

function linkTarget(pageId: string): LinkTarget {
  if (!tree.loaded || tree.byId.has(pageId)) return 'live'
  return tree.trash.some((page) => page.id === pageId) ? 'trash' : 'unavailable'
}

const editor = useEditor({
  extensions: createExtensions({
    placeholder: '',
    checkboxLabel: (checked) => (checked ? t('editor.checkboxDone') : t('editor.checkboxNotDone')),
    brokenLinks: {
      stateOf: linkTarget,
      describe: (state) =>
        state === 'trash' ? t('editor.link.inTrash') : t('editor.link.unavailable'),
    },
    files: {
      formatSize: (bytes) => formatFileSize(bytes, locale.value),
      downloadLabel: (fileName, size) => t('editor.attachment.download', { name: fileName, size }),
    },
  }),
  content: props.content,
  editable: false,
  // As in the page's editor: TipTap's stylesheet would be a <style> element, which the strict policy blocks.
  injectCSS: false,
  enableContentCheck: true,
  onContentError: () => (unreadable.value = true),
  editorProps: {
    attributes: {
      role: 'document',
      'aria-label': props.label,
      class: editorClasses,
    },
  },
})

onBeforeUnmount(() => editor.value?.destroy())
</script>

<template>
  <p v-if="unreadable" role="alert" class="text-body-sm text-error">
    {{ t('history.versionUnreadable') }}
  </p>
  <EditorContent v-else :editor="editor" data-testid="version-content" />
</template>
