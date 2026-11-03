import { useI18n } from 'vue-i18n'

/** "in 45 seconds", "in 15 minutes": the wait, in the language of the page. */
export function formatWait(seconds: number, locale: string): string {
  const formatter = new Intl.RelativeTimeFormat(locale, { numeric: 'always' })
  return seconds < 60
    ? formatter.format(Math.max(1, Math.ceil(seconds)), 'second')
    : formatter.format(Math.ceil(seconds / 60), 'minute')
}

/**
 * Turns an error code from the server into a message in the current language. The keys are written out so
 * the lint rule that finds unused translations can see them; anything unknown gets the generic message.
 */
export function useErrorMessage() {
  const { t, locale } = useI18n()

  return function message(code: string, retryAfterSeconds?: number): string {
    const when = formatWait(retryAfterSeconds ?? 60, locale.value)
    switch (code) {
      case 'invalid_credentials':
        return t('errors.invalid_credentials')
      case 'login_locked':
        return t('errors.login_locked', { when })
      case 'too_many_requests':
        return t('errors.too_many_requests', { when })
      case 'setup_code_invalid':
        return t('errors.setup_code_invalid')
      case 'setup_not_available':
        return t('errors.setup_not_available')
      case 'email_invalid':
        return t('errors.email_invalid')
      case 'display_name_invalid':
        return t('errors.display_name_invalid')
      case 'workspace_name_invalid':
        return t('errors.workspace_name_invalid')
      case 'password_too_short':
        return t('errors.password_too_short')
      case 'password_too_long':
        return t('errors.password_too_long')
      case 'password_same_as_email':
        return t('errors.password_same_as_email')
      case 'not_authenticated':
        return t('errors.not_authenticated')
      case 'forbidden':
        return t('errors.forbidden')
      case 'not_found':
        return t('errors.not_found')
      case 'title_invalid':
        return t('errors.title_invalid')
      case 'parent_invalid':
        return t('errors.parent_invalid')
      case 'parent_in_trash':
        return t('errors.parent_in_trash')
      case 'position_invalid':
        return t('errors.position_invalid')
      case 'move_cycle':
        return t('errors.move_cycle')
      case 'page_in_trash':
        return t('errors.page_in_trash')
      case 'restore_the_parent':
        return t('errors.restore_the_parent')
      case 'too_many_pages':
        return t('errors.too_many_pages')
      case 'attachment_too_large':
        return t('errors.attachment_too_large')
      case 'attachment_empty':
        return t('errors.attachment_empty')
      case 'attachment_not_added':
        return t('errors.attachment_not_added')
      case 'import_empty':
        return t('errors.import_empty')
      case 'import_invalid':
        return t('errors.import_invalid')
      case 'import_too_large':
        return t('errors.import_too_large')
      case 'import_in_progress':
        return t('errors.import_in_progress')
      case 'import_copy_title_invalid':
        return t('errors.import_copy_title_invalid')
      case 'import_too_many_items':
        return t('errors.import_too_many_items')
      case 'sidecar_unavailable':
        return t('errors.sidecar_unavailable')
      case 'import_failed':
        return t('errors.import_failed')
      case 'interrupted':
        return t('errors.interrupted')
      case 'workspace_missing':
        return t('errors.workspace_missing')
      case 'csrf_invalid':
        return t('errors.csrf_invalid')
      case 'network_error':
        return t('errors.network_error')
      case 'server_error':
        return t('errors.server_error')
      default:
        return t('errors.unknown')
    }
  }
}
