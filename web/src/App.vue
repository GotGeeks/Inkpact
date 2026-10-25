<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import LanguageSwitch from './components/LanguageSwitch.vue'

const { t } = useI18n()
</script>

<template>
  <main
    class="mx-auto flex min-h-screen max-w-3xl flex-col gap-space-xl p-margin md:p-margin-desktop"
  >
    <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('app.name') }}</h1>
    <p class="text-body-lg text-on-surface-variant">{{ t('app.tagline') }}</p>
    <LanguageSwitch />
  </main>
</template>
