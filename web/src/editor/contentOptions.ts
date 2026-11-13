import type { Editor } from '@tiptap/core'
import { computed, watch, type Ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatFileSize } from '../fileSize'
import { useTreeStore } from '../stores/tree'
import { refreshBrokenLinks, type BrokenLinksOptions, type LinkTarget } from './brokenLinks'
import type { AttachmentFileOptions } from './attachmentNodes'

/**
 * How a page's content reads, the same in every editor that shows one (the page's own and a version's preview in the
 * history): the labels of a task's checkbox and of a file, and which links point to a page that is in the trash or not to
 * be found. Call it in a component's setup.
 */
export function useContentOptions() {
  const { t, locale } = useI18n()
  const tree = useTreeStore()

  /**
   * What a page that a link points to is now, by the tree this tab has: there, in the trash, or not to be found. Until the
   * tree is read nothing is known, so nothing is marked rather than every link for a moment.
   */
  const trashed = computed(() => new Set(tree.trash.map((page) => page.id)))
  function linkTarget(pageId: string): LinkTarget {
    if (!tree.loaded || tree.byId.has(pageId)) return 'live'
    return trashed.value.has(pageId) ? 'trash' : 'unavailable'
  }

  // Written out, so the lint rule that finds unused translations can see the keys.
  const checkboxLabel = (checked: boolean) =>
    checked ? t('editor.checkboxDone') : t('editor.checkboxNotDone')

  const brokenLinks: BrokenLinksOptions = {
    stateOf: linkTarget,
    describe: (state) =>
      state === 'trash' ? t('editor.link.inTrash') : t('editor.link.unavailable'),
  }

  const files: AttachmentFileOptions = {
    formatSize: (bytes) => formatFileSize(bytes, locale.value),
    downloadLabel: (fileName, size) => t('editor.attachment.download', { name: fileName, size }),
  }

  /**
   * A link to a page that goes to the trash, comes back or is made is marked by the tree, not by the document: when the
   * tree changes, the editor's links are looked at again.
   */
  function keepLinksCurrent(editor: Ref<Editor | undefined>) {
    watch([() => tree.pages, () => tree.trash, () => tree.loaded], () => {
      if (editor.value && !editor.value.isDestroyed) refreshBrokenLinks(editor.value.view)
    })
  }

  return { checkboxLabel, brokenLinks, files, keepLinksCurrent }
}
