<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '../stores/auth'
import { useSyncStore } from '../stores/sync'
import { pwaState } from './registration'

const { t } = useI18n()
const auth = useAuthStore()
const sync = useSyncStore()
const insecure = ref(window.isSecureContext === false)
</script>

<template>
  <div
    v-if="
      pwaState.updateReady || pwaState.registrationFailed || auth.offlineIdentityFailed || insecure
    "
    class="fixed inset-x-space-md bottom-space-md z-50 mx-auto max-w-xl rounded border border-border-strong bg-surface-elevated p-space-md text-body-sm text-on-surface shadow-lg"
  >
    <p v-if="auth.offlineIdentityFailed" role="alert">{{ t('pwa.identityFailed') }}</p>
    <p v-if="pwaState.registrationFailed" role="alert">{{ t('pwa.registrationFailed') }}</p>
    <p v-if="insecure" role="status">{{ t('pwa.httpsRequired') }}</p>
    <button v-if="insecure" type="button" class="min-h-11 underline" @click="insecure = false">
      {{ t('pwa.dismiss') }}
    </button>
    <p v-if="pwaState.updateReady" role="status">
      {{
        sync.unsent || sync.unsafeToLeave || sync.lostEdits.length > 0
          ? t('pwa.updatePendingEdits')
          : t('pwa.updateReady')
      }}
    </p>
  </div>
</template>
