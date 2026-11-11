<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '../stores/auth'
import { useSyncStore } from '../stores/sync'
import { applyUpdate, pwaState } from './registration'

const { t } = useI18n()
const auth = useAuthStore()
const sync = useSyncStore()

// The HTTPS notice is said once per browser, not on every page load: plain HTTP on a LAN is a supported way to run Inkpact.
const httpsNoticeKey = 'inkpact.pwa.httpsNoticeDismissed'
function httpsNoticeDismissed(): boolean {
  try {
    return localStorage.getItem(httpsNoticeKey) === '1'
  } catch {
    return false
  }
}
const insecure = ref(window.isSecureContext === false && !httpsNoticeDismissed())
function dismissHttpsNotice() {
  insecure.value = false
  try {
    localStorage.setItem(httpsNoticeKey, '1')
  } catch {
    // Without storage it is said again next time, which is all that is lost.
  }
}

/** Hidden for this page load; the update stays ready and the notice comes back after a reload. */
const updateLater = ref(false)
const editsPending = computed(() => sync.unsent || sync.unsafeToLeave || sync.lostEdits.length > 0)
const showUpdate = computed(() => pwaState.updateReady && !updateLater.value)
</script>

<template>
  <div
    v-if="showUpdate || pwaState.registrationFailed || auth.offlineIdentityFailed || insecure"
    class="fixed inset-x-space-md bottom-space-md z-50 mx-auto flex max-w-xl flex-col gap-space-sm rounded border border-border-strong bg-surface-elevated p-space-md text-body-sm text-on-surface shadow-lg"
  >
    <p v-if="auth.offlineIdentityFailed" role="alert">{{ t('pwa.identityFailed') }}</p>
    <p v-if="pwaState.registrationFailed" role="alert">{{ t('pwa.registrationFailed') }}</p>
    <template v-if="insecure">
      <p role="status">{{ t('pwa.httpsRequired') }}</p>
      <button
        type="button"
        class="min-h-11 self-start rounded px-space-sm underline focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
        @click="dismissHttpsNotice"
      >
        {{ t('pwa.dismiss') }}
      </button>
    </template>
    <template v-if="showUpdate">
      <p role="status" data-testid="pwa-update">
        {{ editsPending ? t('pwa.updatePendingEdits') : t('pwa.updateReady') }}
      </p>
      <p v-if="pwaState.otherTabsOpen && !editsPending" role="alert">
        {{ t('pwa.closeOtherTabs') }}
      </p>
      <div class="flex flex-wrap gap-space-sm">
        <button
          v-if="!editsPending"
          type="button"
          class="min-h-11 rounded bg-primary-container px-space-lg text-label-md text-on-primary-container focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary md:min-h-9"
          @click="applyUpdate()"
        >
          {{ t('pwa.updateNow') }}
        </button>
        <button
          type="button"
          class="min-h-11 rounded px-space-md text-label-md underline focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
          @click="updateLater = true"
        >
          {{ t('pwa.later') }}
        </button>
      </div>
    </template>
  </div>
</template>
