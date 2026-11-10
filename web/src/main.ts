import { createPinia } from 'pinia'
import { createApp } from 'vue'
import App from './App.vue'
import { applyLocale, createAppI18n, detectLocale } from './i18n'
import { registerPwa } from './pwa/registration'
import { createAppRouter } from './router'
import { applyTheme, detectTheme } from './theme'
import './style.css'

const locale = detectLocale()
applyLocale(locale)

// Before the app mounts. System sets nothing: the stylesheet follows the system setting by itself.
applyTheme(detectTheme())

const pinia = createPinia()
createApp(App).use(pinia).use(createAppI18n(locale)).use(createAppRouter()).mount('#app')

if (import.meta.env.PROD && window.isSecureContext && 'serviceWorker' in navigator) {
  void registerPwa(navigator.serviceWorker)
}
