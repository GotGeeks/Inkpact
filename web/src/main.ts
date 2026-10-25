import { createPinia } from 'pinia'
import { createApp } from 'vue'
import App from './App.vue'
import { applyLocale, createAppI18n, detectLocale } from './i18n'
import { createAppRouter } from './router'
import './style.css'

const locale = detectLocale()
applyLocale(locale)

const pinia = createPinia()
createApp(App).use(pinia).use(createAppI18n(locale)).use(createAppRouter()).mount('#app')
