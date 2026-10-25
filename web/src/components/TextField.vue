<script setup lang="ts">
defineProps<{
  id: string
  label: string
  type?: 'text' | 'email' | 'password'
  autocomplete?: string
  hint?: string
  error?: string
  required?: boolean
}>()

const model = defineModel<string>({ required: true })
</script>

<template>
  <div class="flex flex-col gap-space-sm">
    <label :for="id" class="text-label-md text-on-surface-variant">{{ label }}</label>
    <input
      :id="id"
      v-model="model"
      :type="type ?? 'text'"
      :autocomplete="autocomplete"
      :required="required"
      :aria-invalid="error ? 'true' : undefined"
      :aria-describedby="error ? `${id}-error` : hint ? `${id}-hint` : undefined"
      spellcheck="false"
      autocapitalize="none"
      class="min-h-11 rounded border border-border-subtle bg-surface-canvas px-space-md text-body-lg text-on-surface outline-none focus:border-primary-container focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-container aria-[invalid=true]:border-error md:min-h-9"
    />
    <p v-if="error" :id="`${id}-error`" class="text-body-sm text-error">{{ error }}</p>
    <p v-else-if="hint" :id="`${id}-hint`" class="text-body-sm text-on-surface-variant">
      {{ hint }}
    </p>
  </div>
</template>
