<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useTreeStore } from '../stores/tree'

const { t } = useI18n()
const tree = useTreeStore()
const router = useRouter()

async function create() {
  const change = await tree.create('')
  if (change) await router.push({ name: 'page', params: { pageId: change.page.id } })
}
</script>

<template>
  <section class="mx-auto flex max-w-2xl flex-col gap-space-lg p-margin md:p-margin-desktop">
    <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('start.title') }}</h1>
    <p class="text-body-lg text-on-surface-variant">
      {{ tree.pages.length === 0 ? t('start.empty') : t('start.pick') }}
    </p>
    <button
      v-if="tree.canEdit"
      type="button"
      class="min-h-11 self-start rounded bg-primary-container px-space-lg text-label-md text-on-primary-container hover:opacity-90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary md:min-h-9"
      @click="create"
    >
      {{ t('tree.newPage') }}
    </button>
  </section>
</template>
