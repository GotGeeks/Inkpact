<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import LanguageSwitch from '../components/LanguageSwitch.vue'
import { useAuthStore } from '../stores/auth'

const { t } = useI18n()
const auth = useAuthStore()
const router = useRouter()
const busy = ref(false)

async function leave(everywhere: boolean) {
  if (busy.value) return
  busy.value = true
  try {
    if (everywhere) await auth.logoutEverywhere()
    else await auth.logout()
  } finally {
    busy.value = false
    await router.replace({ name: 'login' })
  }
}

const buttonClass =
  'min-h-11 rounded border border-border-subtle px-space-lg text-label-md hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-container disabled:opacity-50 md:min-h-9'
</script>

<template>
  <main
    class="mx-auto flex min-h-screen w-full max-w-3xl flex-col gap-space-xl p-margin md:p-margin-desktop"
  >
    <header class="flex flex-col gap-space-sm">
      <p class="text-label-md text-on-surface-variant">{{ t('app.name') }}</p>
      <h1 class="text-headline-xl-mobile md:text-headline-xl">
        {{ t('home.signedInAs', { name: auth.user?.displayName }) }}
      </h1>
      <p class="text-body-lg text-on-surface-variant">{{ t('home.placeholder') }}</p>
    </header>
    <div class="flex flex-wrap gap-space-md">
      <button type="button" :disabled="busy" :class="buttonClass" @click="leave(false)">
        {{ t('home.logout') }}
      </button>
      <button type="button" :disabled="busy" :class="buttonClass" @click="leave(true)">
        {{ t('home.logoutEverywhere') }}
      </button>
    </div>
    <LanguageSwitch />
  </main>
</template>
