<script setup lang="ts">
defineProps<{ busy?: boolean }>()
</script>

<template>
  <button
    type="submit"
    :disabled="busy"
    :aria-busy="busy ? 'true' : undefined"
    class="min-h-11 rounded bg-primary-container px-space-lg text-label-md text-on-primary-container hover:opacity-90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary active:scale-98 disabled:opacity-50 md:min-h-9"
  >
    <slot />
  </button>
</template>
