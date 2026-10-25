<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import LanguageSwitch from './LanguageSwitch.vue'

defineProps<{ title: string; intro?: string }>()

const { t } = useI18n()
</script>

<template>
  <main
    class="mx-auto flex min-h-screen w-full max-w-md flex-col justify-center gap-space-xl p-margin md:p-margin-desktop"
  >
    <header class="flex flex-col gap-space-sm">
      <p class="text-label-md text-on-surface-variant">{{ t('app.name') }}</p>
      <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ title }}</h1>
      <p v-if="intro" class="text-body-lg text-on-surface-variant">{{ intro }}</p>
    </header>
    <slot />
    <footer>
      <LanguageSwitch />
    </footer>
  </main>
</template>
