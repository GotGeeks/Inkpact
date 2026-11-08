<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { ApiError, api } from '../api'
import AppIcon from '../components/AppIcon.vue'
import { useChosenInView } from '../editor/useMenuPlacement'
import { useNotificationsStore } from '../stores/notifications'
import { useTagsStore } from '../stores/tags'
import { useTreeStore } from '../stores/tree'
import { findTags, hasExactMatch } from '../tags/tagSearch'
import type { TagRef } from '../tags/types'

const props = defineProps<{ pageId: string; tags: TagRef[]; readOnly: boolean }>()

const { t } = useI18n()
const notifications = useNotificationsStore()
const tree = useTreeStore()
const tagsStore = useTagsStore()

const current = ref<TagRef[]>(props.tags)
watch(
  () => props.tags,
  (next) => (current.value = next),
)

const listboxId = computed(() => `tag-options-${props.pageId}`)
const optionId = (id: string) => `tag-option-${props.pageId}-${id}`

const adding = ref(false)
const query = ref('')
const highlighted = ref(0)
const input = useTemplateRef<HTMLInputElement>('input')
const addButton = useTemplateRef<HTMLButtonElement>('addButton')
const listbox = useTemplateRef<HTMLElement>('listbox')

const excludeIds = computed(() => new Set(current.value.map((tag) => tag.id)))
const suggestions = computed(() => findTags(tagsStore.items, query.value, excludeIds.value))
const showCreate = computed(() => !hasExactMatch(tagsStore.items, query.value))
// "Create tag" first, when it would be offered at all (never for an empty field, and never when a tag of this name
// already exists: typing it then just picks that tag, as the slash menu's rule is for pages).
const options = computed((): Array<{ id: string; name: string }> => {
  const trimmed = query.value.trim()
  const create = trimmed !== '' && showCreate.value ? [{ id: 'create', name: trimmed }] : []
  return [...create, ...suggestions.value]
})

watch(options, () => {
  if (highlighted.value >= options.value.length) highlighted.value = 0
})

const activeOption = computed(() => options.value[highlighted.value] ?? null)

useChosenInView(listbox, () => (activeOption.value ? optionId(activeOption.value.id) : null))

async function startAdding() {
  adding.value = true
  query.value = ''
  highlighted.value = 0
  await tagsStore.ensureLoaded().catch(() => undefined)
  await nextTick()
  input.value?.focus()
}

function cancelAdding() {
  adding.value = false
  addButton.value?.focus()
}

async function change(body: { add?: string[]; remove?: string[] }) {
  try {
    current.value = await api<TagRef[]>('POST', `/api/pages/${props.pageId}/tags`, body)
  } catch (error) {
    notifications.post({ type: 'error', code: error instanceof ApiError ? error.code : 'unknown' })
    return
  }

  // The workspace list's suggestions and counts follow: a new tag is offered next time, another page's count changed.
  void tagsStore.load().catch(() => undefined)
}

async function choose(option: { id: string; name: string }) {
  await change({ add: [option.name] })
  query.value = ''
  highlighted.value = 0
  await nextTick()
  input.value?.focus()
}

function removeTag(tag: TagRef) {
  void change({ remove: [tag.id] })
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    cancelAdding()
  } else if (event.key === 'ArrowDown') {
    event.preventDefault()
    if (options.value.length > 0) highlighted.value = (highlighted.value + 1) % options.value.length
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    if (options.value.length > 0) {
      highlighted.value = (highlighted.value - 1 + options.value.length) % options.value.length
    }
  } else if (event.key === 'Enter') {
    event.preventDefault()
    const option = options.value[highlighted.value]
    if (option) void choose(option)
  }
}

/** Reads the page's tags again, as the backlinks panel does for its own list: the server is the source of truth. */
async function refresh() {
  try {
    const detail = await tree.fetchPage(props.pageId)
    if (detail) current.value = detail.tags
  } catch {
    // A convenience refresh; the list already on screen stays.
  }
}

function onVisible() {
  if (document.visibilityState === 'visible') void refresh()
}

onMounted(() => document.addEventListener('visibilitychange', onVisible))
onBeforeUnmount(() => document.removeEventListener('visibilitychange', onVisible))
</script>

<template>
  <div
    v-if="current.length > 0 || !readOnly"
    class="flex flex-wrap items-center gap-space-sm"
    role="list"
    :aria-label="t('tags.label')"
  >
    <span
      v-for="tag in current"
      :key="tag.id"
      role="listitem"
      class="inline-flex min-h-8 items-center gap-space-xs rounded-full bg-surface-container-high px-space-md text-label-md text-on-surface pointer-coarse:min-h-11"
    >
      <RouterLink
        :to="{ name: 'tag', params: { tagId: tag.id } }"
        class="rounded focus-visible:outline-2 focus-visible:outline-primary"
      >
        <span class="before:content-['#']">{{ tag.name }}</span>
      </RouterLink>
      <button
        v-if="!readOnly"
        type="button"
        class="flex size-5 shrink-0 items-center justify-center rounded-full text-on-surface-variant hover:text-on-surface focus-visible:outline-2 focus-visible:outline-primary pointer-coarse:size-11"
        :aria-label="t('tags.remove', { name: tag.name })"
        @click="removeTag(tag)"
      >
        <AppIcon name="close" class="size-3" />
      </button>
    </span>

    <div v-if="!readOnly && !adding">
      <button
        ref="addButton"
        type="button"
        class="flex min-h-8 items-center gap-space-xs rounded-full border border-border-subtle px-space-md text-label-md text-on-surface-variant hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary pointer-coarse:min-h-11"
        data-testid="add-tag-button"
        @click="startAdding"
      >
        <AppIcon name="plus" class="size-3" />
        {{ t('tags.add') }}
      </button>
    </div>

    <div v-else-if="!readOnly" class="relative">
      <input
        ref="input"
        v-model="query"
        type="text"
        role="combobox"
        aria-autocomplete="list"
        :aria-expanded="options.length > 0"
        :aria-controls="listboxId"
        :aria-activedescendant="activeOption ? optionId(activeOption.id) : undefined"
        :aria-label="t('tags.addLabel')"
        data-testid="add-tag-field"
        class="min-h-8 w-40 rounded border border-border-subtle bg-surface-canvas px-space-md text-body-sm text-on-surface focus-visible:outline-2 focus-visible:outline-primary pointer-coarse:min-h-11"
        :placeholder="t('tags.addPlaceholder')"
        @keydown="onKeydown"
        @blur="cancelAdding"
      />
      <div
        v-show="options.length > 0"
        :id="listboxId"
        ref="listbox"
        role="listbox"
        :aria-label="t('tags.addLabel')"
        class="absolute start-0 top-full z-10 mt-space-xs w-56 max-h-60 overflow-y-auto rounded-lg border border-border-subtle bg-surface-subtle p-space-xs shadow-lg"
      >
        <div
          v-for="(option, index) in options"
          :id="optionId(option.id)"
          :key="option.id"
          role="option"
          :aria-selected="index === highlighted"
          class="flex min-h-9 cursor-pointer items-center rounded-md px-space-md text-body-sm pointer-coarse:min-h-11"
          :class="index === highlighted ? 'bg-surface-elevated text-primary' : 'text-on-surface'"
          @mousedown.prevent
          @mousemove="highlighted = index"
          @click="choose(option)"
        >
          {{ option.id === 'create' ? t('tags.create', { name: option.name }) : option.name }}
        </div>
      </div>
    </div>
  </div>
</template>
