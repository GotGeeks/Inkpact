<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import { useTreeStore } from '../stores/tree'

const { t, d } = useI18n()
const tree = useTreeStore()
</script>

<template>
  <section class="mx-auto flex max-w-3xl flex-col gap-space-lg p-margin md:p-margin-desktop">
    <header class="flex flex-col gap-space-xs">
      <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('trash.title') }}</h1>
      <p class="text-body-sm text-on-surface-variant">{{ t('trash.intro') }}</p>
    </header>

    <p v-if="tree.trash.length === 0" class="text-body-lg text-on-surface-variant">
      {{ t('trash.empty') }}
    </p>

    <ul v-else class="flex flex-col gap-space-sm" data-testid="trash-list">
      <li
        v-for="page in tree.trash"
        :key="page.id"
        class="flex items-center gap-space-md rounded border border-border-subtle bg-surface-panel p-space-md"
      >
        <AppIcon name="page" class="text-on-surface-variant" />
        <div class="min-w-0 flex-1">
          <p
            class="truncate text-body-lg"
            :class="page.title ? '' : 'text-on-surface-variant italic'"
          >
            {{ page.title || t('tree.untitled') }}
          </p>
          <p v-if="page.deletedAt" class="text-body-sm text-on-surface-variant">
            {{ t('trash.deleted', { when: d(new Date(page.deletedAt), 'trash') }) }}
          </p>
        </div>
        <button
          v-if="tree.canEdit"
          type="button"
          class="flex min-h-11 items-center gap-space-xs rounded border border-border-strong px-space-md text-label-md hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
          :aria-label="t('trash.restoreNamed', { title: page.title || t('tree.untitled') })"
          @click="tree.restore(page.id)"
        >
          <AppIcon name="restore" />
          {{ t('trash.restore') }}
        </button>
      </li>
    </ul>
  </section>
</template>
