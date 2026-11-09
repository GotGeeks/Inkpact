<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute } from 'vue-router'
import { ApiError } from '../api'
import { useNotificationsStore } from '../stores/notifications'
import { useTagsStore } from '../stores/tags'
import { useTreeStore } from '../stores/tree'
import type { TagPage } from './types'

const { t, n } = useI18n()
const route = useRoute()
const tagsStore = useTagsStore()
const tree = useTreeStore()
const notifications = useNotificationsStore()

const tagId = computed(() => String(route.params.tagId))
const name = ref('')
const draft = ref('')
const items = ref<TagPage[]>([])
const more = ref(false)
const notFound = ref(false)
const loaded = ref(false)

async function load() {
  const id = tagId.value
  loaded.value = false
  notFound.value = false
  try {
    const result = await tagsStore.pagesOf(id)
    if (id !== tagId.value) return
    name.value = result.tagName
    draft.value = result.tagName
    items.value = result.items
    more.value = result.more
  } catch (error) {
    if (id !== tagId.value) return
    if (error instanceof ApiError && error.status === 404) {
      notFound.value = true
    } else {
      notifications.post({
        type: 'error',
        code: error instanceof ApiError ? error.code : 'unknown',
      })
    }
  } finally {
    if (id === tagId.value) loaded.value = true
  }
}

watch(tagId, load, { immediate: true })

async function save() {
  const trimmed = draft.value.trim()
  if (!tree.canEdit || trimmed === name.value || trimmed === '') {
    draft.value = name.value
    return
  }

  try {
    const renamed = await tagsStore.rename(tagId.value, trimmed)
    name.value = renamed.name
    draft.value = renamed.name
  } catch (error) {
    notifications.post({ type: 'error', code: error instanceof ApiError ? error.code : 'unknown' })
    draft.value = name.value
  }
}
</script>

<template>
  <section
    v-if="notFound"
    class="mx-auto flex max-w-2xl flex-col gap-space-md p-margin md:p-margin-desktop"
  >
    <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('tags.notFound') }}</h1>
    <RouterLink :to="{ name: 'tags' }" class="self-start text-primary underline">{{
      t('tags.backToTags')
    }}</RouterLink>
  </section>
  <section
    v-else-if="loaded"
    class="mx-auto flex max-w-3xl flex-col gap-space-lg p-margin md:p-margin-desktop"
  >
    <header class="flex flex-col gap-space-xs">
      <input
        v-if="tree.canEdit"
        v-model="draft"
        type="text"
        :aria-label="t('tags.nameLabel')"
        data-testid="tag-name-field"
        class="w-full rounded bg-transparent text-headline-xl-mobile text-on-surface pointer-coarse:min-h-11 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary md:text-headline-xl"
        @blur="save"
        @keydown.enter.prevent="($event.target as HTMLInputElement).blur()"
      />
      <h1 v-else class="text-headline-xl-mobile md:text-headline-xl">{{ name }}</h1>
    </header>

    <p v-if="items.length === 0" class="text-body-lg text-on-surface-variant">
      {{ t('tags.noPages') }}
    </p>

    <template v-else>
      <p class="text-label-md text-on-surface-variant uppercase">
        {{
          more
            ? t('tags.pagesMany', { count: n(items.length) })
            : t('tags.pageCount', { count: n(items.length) }, items.length)
        }}
      </p>
      <ul class="flex flex-col gap-space-sm" data-testid="tag-pages-list">
        <li v-for="page in items" :key="page.id">
          <RouterLink
            :to="{ name: 'page', params: { pageId: page.id } }"
            class="flex min-h-11 items-center gap-space-md rounded border border-border-subtle bg-surface-panel px-space-md text-body-lg hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
          >
            <span
              class="min-w-0 flex-1 truncate"
              :class="page.title ? '' : 'italic text-on-surface-variant'"
            >
              {{ page.title || t('tree.untitled') }}
            </span>
            <span
              v-if="page.parentTitle"
              class="max-w-[40%] shrink-0 truncate text-label-md text-on-surface-variant"
            >
              {{ page.parentTitle }}
            </span>
          </RouterLink>
        </li>
      </ul>
    </template>
  </section>
</template>
