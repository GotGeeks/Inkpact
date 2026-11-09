<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { ApiError } from '../api'
import { fold } from '../editor/pageSearch'
import { useNotificationsStore } from '../stores/notifications'
import { useTagsStore } from '../stores/tags'

const { t, n } = useI18n()
const tagsStore = useTagsStore()
const notifications = useNotificationsStore()
const query = ref('')

// A list read before (the add-tag field reads it too) shows at once while it is read again; otherwise the view says it
// is loading, and never "no tags yet" before it knows.
const state = ref<'loading' | 'ready' | 'failed'>(tagsStore.loaded ? 'ready' : 'loading')

onMounted(async () => {
  try {
    await tagsStore.load()
    state.value = 'ready'
  } catch (error) {
    notifications.post({ type: 'error', code: error instanceof ApiError ? error.code : 'unknown' })
    if (!tagsStore.loaded) state.value = 'failed'
  }
})

const filtered = computed(() => {
  const wanted = fold(query.value.trim())
  const items =
    wanted === ''
      ? tagsStore.items
      : tagsStore.items.filter((tag) => fold(tag.name).includes(wanted))
  return [...items].sort((a, b) => a.name.localeCompare(b.name))
})
</script>

<template>
  <section class="mx-auto flex max-w-3xl flex-col gap-space-lg p-margin md:p-margin-desktop">
    <header class="flex flex-col gap-space-xs">
      <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('tags.title') }}</h1>
      <p class="text-body-sm text-on-surface-variant">{{ t('tags.intro') }}</p>
    </header>

    <p v-if="state === 'loading'" role="status" class="text-body-lg text-on-surface-variant">
      {{ t('tags.loading') }}
    </p>

    <p v-else-if="state === 'failed'" class="text-body-lg text-on-surface-variant">
      {{ t('tags.loadFailed') }}
    </p>

    <p v-else-if="tagsStore.items.length === 0" class="text-body-lg text-on-surface-variant">
      {{ t('tags.empty') }}
    </p>

    <template v-else>
      <input
        v-model="query"
        type="text"
        :aria-label="t('tags.filterLabel')"
        :placeholder="t('tags.filterPlaceholder')"
        data-testid="tags-filter"
        class="min-h-11 rounded border border-border-subtle bg-surface-canvas px-space-md text-body-sm text-on-surface focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
      />

      <p
        v-if="filtered.length === 0"
        role="status"
        class="text-body-lg text-on-surface-variant"
        data-testid="tags-no-match"
      >
        {{ t('tags.noMatch', { query: query.trim() }) }}
      </p>

      <ul v-else class="flex flex-col gap-space-sm" data-testid="tags-list">
        <li v-for="tag in filtered" :key="tag.id">
          <RouterLink
            :to="{ name: 'tag', params: { tagId: tag.id } }"
            class="flex min-h-11 items-center gap-space-md rounded border border-border-subtle bg-surface-panel px-space-md text-body-lg hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
          >
            <span class="min-w-0 flex-1 truncate before:content-['#']">{{ tag.name }}</span>
            <span class="shrink-0 rounded-full bg-surface-container-high px-space-sm text-label-xs">
              {{ n(tag.pageCount) }}
            </span>
          </RouterLink>
        </li>
      </ul>
    </template>
  </section>
</template>
