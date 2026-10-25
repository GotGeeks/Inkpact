<script setup lang="ts">
import { ToggleGroupItem, ToggleGroupRoot } from 'reka-ui'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { applyLocale, locales, type Locale } from '../i18n'
import { useSettingsStore } from '../stores/settings'

const { t, locale } = useI18n()
const settings = useSettingsStore()

// Static keys, so the unused-keys lint rule can see them.
const names = computed(() => ({ en: t('language.en'), nl: t('language.nl') }))

function select(next: unknown) {
  // Reka reports undefined when the active item is pressed again; keep the current language.
  if (typeof next !== 'string') return
  settings.setLocale(next as Locale)
  locale.value = next
  applyLocale(next as Locale)
}
</script>

<template>
  <ToggleGroupRoot
    type="single"
    :model-value="locale"
    :aria-label="t('language.label')"
    class="inline-flex gap-space-xs rounded-lg border border-border-subtle bg-surface-panel p-space-xs"
    @update:model-value="select"
  >
    <ToggleGroupItem
      v-for="code in locales"
      :key="code"
      :value="code"
      class="min-h-11 rounded px-space-lg text-label-md text-on-surface-variant hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-container data-[state=on]:bg-primary-container data-[state=on]:text-on-primary-container md:min-h-9"
    >
      {{ names[code] }}
    </ToggleGroupItem>
  </ToggleGroupRoot>
</template>
