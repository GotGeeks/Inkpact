import { createPinia } from 'pinia'
import { createApp } from 'vue'
import App from './App.vue'
import { createAppI18n } from './i18n'
import './style.css'

createApp(App).use(createPinia()).use(createAppI18n()).mount('#app')
