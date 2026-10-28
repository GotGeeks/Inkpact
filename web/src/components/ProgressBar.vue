<script setup lang="ts">
// The browser's own progress element, so that screen readers get a progress bar with a value for free. Without a
// value it is indeterminate, which the browser animates. Styled with Tailwind variants, since no inline style may be used.
defineProps<{ label: string; value?: number | null; max?: number }>()
</script>

<template>
  <progress
    :aria-label="label"
    :value="value ?? undefined"
    :max="max ?? 100"
    class="h-2 w-full appearance-none overflow-hidden rounded-full bg-surface-container-high [&::-moz-progress-bar]:bg-primary-container [&::-webkit-progress-bar]:bg-surface-container-high [&::-webkit-progress-value]:bg-primary-container"
  ></progress>
</template>
