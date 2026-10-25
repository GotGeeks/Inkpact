import { useI18n } from 'vue-i18n'

/**
 * Turns an error code from the server into a message in the current language. The keys are written out so
 * the lint rule that finds unused translations can see them; anything unknown gets the generic message.
 */
export function useErrorMessage() {
  const { t } = useI18n()

  return function message(code: string): string {
    switch (code) {
      case 'invalid_credentials':
        return t('errors.invalid_credentials')
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
      case 'network_error':
        return t('errors.network_error')
      case 'server_error':
        return t('errors.server_error')
      default:
        return t('errors.unknown')
    }
  }
}
