<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { safeRedirect } from '../router'
import { useAuthStore } from '../stores/auth'

const { t } = useI18n()
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const failed = ref(false)

async function retry() {
  failed.value = false
  try {
    await auth.load()
    await router.replace(safeRedirect(route.query.redirect) ?? '/')
  } catch (error) {
    console.error('The online session could not be checked', error)
    failed.value = true
  }
}
</script>

<template>
  <main
    class="mx-auto flex max-w-2xl flex-col items-start gap-space-md p-margin md:p-margin-desktop"
  >
    <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('offline.unavailableTitle') }}</h1>
    <p class="text-body-sm">{{ t('offline.unavailable') }}</p>
    <p v-if="failed" role="alert" class="text-body-sm text-error">{{ t('offline.retryFailed') }}</p>
    <button
      type="button"
      class="min-h-11 rounded border border-border-strong px-space-lg text-label-md focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
      @click="retry"
    >
      {{ t('offline.retry') }}
    </button>
  </main>
</template>
