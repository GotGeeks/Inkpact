import { useI18n } from 'vue-i18n'
import type { ImportReason } from './importApi'

/**
 * Turns the codes of an import's report into text in the language of the page. The keys are written out so that the
 * lint rule that finds unused translations can see them. A reason's `detail` is data (a tag name, a file name) that goes
 * into the sentence; an unknown code gets the generic message, so a newer server never shows a raw code.
 */
export function useImportMessages() {
  const { t, n } = useI18n()

  function reason(item: ImportReason): string {
    const detail = item.detail ?? ''
    const text = reasonText(item.code, detail)
    return item.count > 1
      ? t('import.reasons.repeated', { message: text, count: n(item.count) })
      : text
  }

  function reasonText(code: string, detail: string): string {
    switch (code) {
      case 'unsupported_element':
        return t('import.reasons.unsupported_element', { detail })
      case 'removed_element':
        return t('import.reasons.removed_element', { detail })
      case 'link_removed':
        return detail === 'relative'
          ? t('import.reasons.link_removed_relative')
          : t('import.reasons.link_removed', { detail })
      case 'note_link_pending':
        return t('import.reasons.note_link_pending')
      case 'resource_missing':
        return t('import.reasons.resource_missing')
      case 'resource_link_text_lost':
        return t('import.reasons.resource_link_text_lost')
      case 'external_image':
        return t('import.reasons.external_image')
      case 'image_not_kept':
        return t('import.reasons.image_not_kept')
      case 'footnote':
        return t('import.reasons.footnote')
      case 'definition_list':
        return t('import.reasons.definition_list')
      case 'math':
        return t('import.reasons.math')
      case 'highlight':
        return t('import.reasons.highlight')
      case 'sub_superscript':
        return t('import.reasons.sub_superscript')
      case 'mermaid':
        return t('import.reasons.mermaid')
      case 'nesting_flattened':
        return t('import.reasons.nesting_flattened')
      case 'mark_dropped':
        return t('import.reasons.mark_dropped')
      case 'truncated':
        return t('import.reasons.truncated')
      case 'encrypted':
        return t('import.reasons.encrypted')
      case 'item_too_large':
        return t('import.reasons.item_too_large')
      case 'resource_too_large':
        return t('import.reasons.resource_too_large', { detail })
      case 'resource_empty':
        return t('import.reasons.resource_empty', { detail })
      case 'notebook_missing':
        return t('import.reasons.notebook_missing')
      case 'notebook_cycle':
        return t('import.reasons.notebook_cycle')
      case 'notebook_in_trash':
        return t('import.reasons.notebook_in_trash')
      case 'parent_failed':
        return t('import.reasons.parent_failed')
      case 'conversion_failed':
        return t('import.reasons.conversion_failed')
      case 'item_failed':
        return t('import.reasons.item_failed')
      default:
        return t('import.reasons.unknown')
    }
  }

  function status(value: string): string {
    switch (value) {
      case 'imperfect':
        return t('import.status.imperfect')
      case 'conflict':
        return t('import.status.conflict')
      case 'skipped':
        return t('import.status.skipped')
      case 'failed':
        return t('import.status.failed')
      default:
        return t('import.status.imperfect')
    }
  }

  function kind(value: string): string {
    switch (value) {
      case 'notebook':
        return t('import.kind.notebook')
      case 'tag':
        return t('import.kind.tag')
      default:
        return t('import.kind.note')
    }
  }

  return { reason, status, kind }
}
