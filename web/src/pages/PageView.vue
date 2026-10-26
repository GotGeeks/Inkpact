<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute } from 'vue-router'
import { useTreeStore } from '../stores/tree'
import type { PageDetail } from '../tree/types'

const { t } = useI18n()
const route = useRoute()
const tree = useTreeStore()

const pageId = computed(() => String(route.params.pageId))
const detail = ref<PageDetail | null>(null)
const missing = ref(false)

// Read again when the page changes and when anything in the tree does, so a rename or a trash shows here at once.
watch(
  [pageId, () => tree.treeVersion],
  async ([id]) => {
    const found = await tree.fetchPage(id)
    // The person may have moved on while this was loading.
    if (id !== pageId.value) return
    detail.value = found
    missing.value = found === null
  },
  { immediate: true },
)

const ownTrash = computed(() => detail.value?.page.deletedAt !== undefined)
const title = computed(() => detail.value?.page.title || t('tree.untitled'))
</script>

<template>
  <section
    v-if="missing"
    class="mx-auto flex max-w-2xl flex-col gap-space-md p-margin md:p-margin-desktop"
  >
    <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('page.notFound') }}</h1>
    <RouterLink :to="{ name: 'home' }" class="self-start text-primary underline">{{
      t('page.backToStart')
    }}</RouterLink>
  </section>

  <article
    v-else-if="detail"
    class="mx-auto flex max-w-3xl flex-col gap-space-lg p-margin md:p-margin-desktop"
  >
    <div
      v-if="detail.inTrash"
      role="status"
      class="flex flex-wrap items-center gap-space-md rounded border border-border-strong bg-surface-elevated p-space-md text-body-sm"
    >
      <p class="flex-1">{{ ownTrash ? t('page.inTrash') : t('page.inTrashThroughParent') }}</p>
      <button
        v-if="ownTrash && tree.canEdit"
        type="button"
        class="min-h-11 rounded border border-border-strong px-space-lg text-label-md hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
        @click="tree.restore(detail.page.id)"
      >
        {{ t('trash.restore') }}
      </button>
    </div>

    <h1
      class="text-headline-xl-mobile md:text-headline-xl"
      :class="detail.page.title ? '' : 'text-on-surface-variant'"
    >
      {{ title }}
    </h1>
    <p class="text-body-lg text-on-surface-variant">{{ t('page.placeholder') }}</p>
  </article>
</template>
