import { computed, watchEffect } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useCurrentPageStore } from '../stores/currentPage'

/**
 * The tab's title names the screen, or the page that is open ("Meeting notes – Inkpact"): it is what a screen reader says
 * when the tab is reached and what the browser's history shows (WCAG 2.4.2). It follows the language and renames.
 */
export function useDocumentTitle() {
  const { t } = useI18n()
  const route = useRoute()
  const current = useCurrentPageStore()

  const screen = computed<string | null>(() => {
    switch (route.name) {
      case 'page': {
        // The page as the view fetched it (also one in the trash); until then it is not known.
        if (current.id !== String(route.params.pageId)) return null
        return current.missing ? t('page.notFound') : current.title || t('tree.untitled')
      }
      case 'trash':
        return t('trash.title')
      case 'import':
        return t('import.title')
      case 'login':
        return t('login.title')
      case 'setup':
        return t('setup.title')
      case 'home':
        return t('start.title')
      default:
        return null
    }
  })

  watchEffect(() => {
    document.title = screen.value
      ? t('a11y.documentTitle', { screen: screen.value, app: t('app.name') })
      : t('app.name')
  })
}
