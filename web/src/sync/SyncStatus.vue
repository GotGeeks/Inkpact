<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useSyncStore } from '../stores/sync'

const { t } = useI18n()
const sync = useSyncStore()

// Written out, so the lint rule that finds unused translations can see the keys.
const label = computed(() => {
  switch (sync.display) {
    case 'live':
      return t('sync.status.live')
    case 'offline':
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
    class="inline-flex items-center gap-space-sm rounded-full border border-border-subtle bg-surface-subtle px-space-md py-space-xs text-label-xs text-on-surface-variant"
  >
    <span class="size-1.5 rounded-full" :class="dot" aria-hidden="true" />
    {{ label }}
  </span>
</template>
