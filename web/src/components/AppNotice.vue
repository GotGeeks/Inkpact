<script setup lang="ts">
/**
 * A message across the top of a screen: `info` for something to know (said politely to a screen reader), `error` for
 * something that went wrong (said at once). The text is the default slot; a button for it goes in `action`.
 */
withDefaults(defineProps<{ tone?: 'info' | 'error' }>(), { tone: 'info' })

const tones = {
  info: 'border-border-subtle bg-surface-panel text-on-surface',
  error: 'border-error bg-error-container text-on-error-container',
}
</script>

<template>
  <div
    :role="tone === 'error' ? 'alert' : 'status'"
    :data-tone="tone"
    class="flex flex-wrap items-center gap-x-space-md gap-y-space-sm border-b p-space-md text-body-sm"
    :class="tones[tone]"
  >
    <p class="flex-1"><slot /></p>
    <slot name="action" />
  </div>
</template>
