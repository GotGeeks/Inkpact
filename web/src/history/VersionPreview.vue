<script setup lang="ts">
import type { JSONContent } from '@tiptap/core'
import { EditorContent, useEditor } from '@tiptap/vue-3'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useContentOptions } from '../editor/contentOptions'
import { editorClasses } from '../editor/editorClasses'
import { createExtensions } from '../editor/extensions'

/**
 * A version's content, read-only, in an editor of its own built from the page editor's extensions and its content options
 * (`useContentOptions`): the same schema, the same rules for links (an address is checked when it is rendered, a link to a
 * page in the trash is struck through) and for files (by id, from the app's own address). It has no Yjs document and no
 * socket, so nothing it does reaches the page that is open.
 *
 * Its words (the label, a checkbox's, a file's) are set when it is made: the panel makes it again when the language changes.
 */
const props = defineProps<{
  content: JSONContent
  /** What a screen reader calls the content. */
  label: string
}>()

const { t } = useI18n()
const options = useContentOptions()

/** The content does not fit this app's schema (a newer app made it): it is not shown in part. */
const unreadable = ref(false)

const editor = useEditor({
  extensions: createExtensions({
    placeholder: '',
    checkboxLabel: options.checkboxLabel,
    brokenLinks: options.brokenLinks,
    files: options.files,
    // A click on a link is taken and does nothing: the panel is for looking at a version, and leaving the page from it
    // (or a new tab, or the mail program for a mailto: link) is not what a click there means. Without this the browser
    // would follow the link, as the editor is read-only.
    openLink: () => {},
  }),
  content: props.content,
  editable: false,
  // As in the page's editor: TipTap's stylesheet would be a <style> element, which the strict policy blocks.
  injectCSS: false,
  enableContentCheck: true,
  onContentError: () => (unreadable.value = true),
  editorProps: {
    attributes: { role: 'document', 'aria-label': props.label, class: editorClasses },
  },
})
// useEditor destroys the editor when the component goes.
options.keepLinksCurrent(editor)
</script>

<template>
  <p v-if="unreadable" role="alert" class="text-body-sm text-error">
    {{ t('history.versionUnreadable') }}
  </p>
  <EditorContent v-else :editor="editor" data-testid="version-content" />
</template>
