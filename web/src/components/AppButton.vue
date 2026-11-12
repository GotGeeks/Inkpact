<script setup lang="ts">
/**
 * The app's button, so that its size, focus ring and looks are written once. 44 pixels high on a phone and 36 from
 * the `md` breakpoint. `primary` is the one main action of a screen, `outline` any other action, `text` a quiet one
 * beside them, and `contained` an action inside a coloured box (an error notice), which takes the box's own text
 * colour for its border. A submit button of a form is `SubmitButton`.
 */
withDefaults(defineProps<{ variant?: 'primary' | 'outline' | 'text' | 'contained' }>(), {
  variant: 'outline',
})

const variants = {
  primary: 'bg-primary-container text-on-primary-container hover:opacity-90',
  outline: 'border border-border-strong hover:bg-surface-container-high',
  text: 'text-primary hover:bg-surface-container-high',
  contained: 'border border-current hover:opacity-80',
}
</script>

<template>
  <button
    type="button"
    :data-variant="variant"
    class="min-h-11 rounded px-space-lg text-label-md focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary disabled:opacity-50 md:min-h-9"
    :class="variants[variant]"
  >
    <slot />
  </button>
</template>
