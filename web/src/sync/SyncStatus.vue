<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSyncStore } from '../stores/sync'

const { t } = useI18n()
const sync = useSyncStore()
const props = defineProps<{ pageId?: string }>()
const local = computed(() => (props.pageId ? sync.localStatus(props.pageId) : null))
const pageUnsent = computed(() => (props.pageId ? !!sync.pages[props.pageId]?.unsent : sync.unsent))

// Written out, so the lint rule that finds unused translations can see the keys.
const label = computed(() => {
  switch (sync.display) {
    case 'live':
      return pageUnsent.value ? t('offline.sending') : t('sync.status.live')
    case 'offline':
      if (local.value?.error) return t('offline.notSaved')
      if (local.value?.pending) return t('offline.saving')
      if (local.value) return pageUnsent.value ? t('offline.savedUnsynced') : t('offline.saved')
      return t('sync.status.offline')
    default:
      return t('sync.status.connecting')
  }
})
const dot = computed(() => (sync.display === 'live' ? 'bg-sync-active' : 'bg-sync-offline'))
</script>

<template>
  <span
    role="status"
    data-testid="sync-status"
    :data-state="sync.display"
    :data-unsent="sync.unsent"
    :data-local="
      local?.error ? 'error' : local?.pending ? 'saving' : local ? 'saved' : 'unavailable'
    "
    class="inline-flex items-center gap-space-sm rounded-full border border-border-subtle bg-surface-subtle px-space-md py-space-xs text-label-xs text-on-surface-variant"
  >
    <span class="size-1.5 rounded-full" :class="dot" aria-hidden="true" />
    {{ label }}
    <span v-if="sync.display !== 'offline' && local?.pending">{{ t('offline.saving') }}</span>
    <span v-else-if="sync.display !== 'offline' && local?.error">{{ t('offline.notSaved') }}</span>
    <span v-else-if="sync.display !== 'offline' && local">{{ t('offline.savedHere') }}</span>
  </span>
</template>
