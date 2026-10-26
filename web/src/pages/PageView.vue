<script setup lang="ts">
import { computed, ref, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute } from 'vue-router'
import PageEditor from '../editor/PageEditor.vue'
import { useSyncStore } from '../stores/sync'
import { useTreeStore } from '../stores/tree'
import SyncStatus from '../sync/SyncStatus.vue'
import TitleField from './TitleField.vue'
import type { PageDetail } from '../tree/types'

const { t } = useI18n()
const route = useRoute()
const tree = useTreeStore()
const sync = useSyncStore()

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

// The editor opens the page's content over the tab's one socket; the view only reads what the socket says about it.
const syncState = computed(() => sync.pages[pageId.value]?.state)
// Remembered: showing the not-found message unmounts the editor, which leaves the page and clears its state, and the
// editor would come back to ask again.
const missingOnSocket = ref(false)
watch(pageId, () => (missingOnSocket.value = false))
watch(syncState, (state) => {
  if (state === 'missing') missingOnSocket.value = true
})
const notFound = computed(() => missing.value || missingOnSocket.value)

const ownTrash = computed(() => detail.value?.page.deletedAt !== undefined)
const editor = useTemplateRef<InstanceType<typeof PageEditor>>('editor')
</script>

<template>
  <section
    v-if="notFound"
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

    <div class="flex items-center justify-end">
      <SyncStatus />
    </div>

    <p
      v-if="syncState === 'tooLarge'"
      role="alert"
      class="rounded border border-error p-space-md text-body-sm text-error"
    >
      {{ t('sync.tooLarge') }}
    </p>

    <!-- The title is metadata of the page tree, not part of the document. The content of a page in the trash stays editable. -->
    <TitleField
      :key="`title-${detail.page.id}`"
      :page-id="detail.page.id"
      :title="detail.page.title"
      :read-only="!tree.canEdit || detail.inTrash"
      @enter="editor?.focus()"
    />
    <PageEditor
      :key="detail.page.id"
      ref="editor"
      :page-id="detail.page.id"
      :read-only="!tree.canEdit"
    />
  </article>
</template>
