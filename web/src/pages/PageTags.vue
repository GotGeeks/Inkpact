<script setup lang="ts">
import { computed, nextTick, ref, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { ApiError, api } from '../api'
import AppIcon from '../components/AppIcon.vue'
import { useOnTabVisible } from '../composables/onTabVisible'
import { useChosenInView } from '../editor/useMenuPlacement'
import { useNotificationsStore } from '../stores/notifications'
import { useTagsStore } from '../stores/tags'
import { useTreeStore } from '../stores/tree'
import { cleanTagName, findTags, hasExactMatch } from '../tags/tagSearch'
import type { TagRef } from '../tags/types'

const props = defineProps<{ pageId: string; tags: TagRef[]; readOnly: boolean }>()

const { t } = useI18n()
const notifications = useNotificationsStore()
const tree = useTreeStore()
const tagsStore = useTagsStore()

// The page's tags as the server last said. Seeded from the page the view read (the view gives every page its own row),
// then only from this row's own requests: a later read of the page by the view could be older than a change made here.
const current = ref<TagRef[]>(props.tags)

const listboxId = computed(() => `tag-options-${props.pageId}`)
const optionId = (id: string) => `tag-option-${props.pageId}-${id}`

const adding = ref(false)
const query = ref('')
const highlighted = ref(0)
const row = useTemplateRef<HTMLElement>('row')
const input = useTemplateRef<HTMLInputElement>('input')
const addButton = useTemplateRef<HTMLButtonElement>('addButton')
const listbox = useTemplateRef<HTMLElement>('listbox')

const excludeIds = computed(() => new Set(current.value.map((tag) => tag.id)))
const suggestions = computed(() => findTags(tagsStore.items, query.value, excludeIds.value))
// "Create tag" first, when it would be offered at all: never for an empty field, and never when a tag of this name
// already exists by the server's rule (case ignored, accents not), since typing it would then just pick that tag.
const options = computed((): Array<{ id: string; name: string }> => {
  const name = cleanTagName(query.value)
  const create =
    name !== '' && !hasExactMatch(tagsStore.items, name) ? [{ id: 'create', name }] : []
  return [...create, ...suggestions.value]
})

// A new list starts at its first option: an index kept from the old one would point at something else.
watch(query, () => (highlighted.value = 0))
watch(options, () => {
  if (highlighted.value >= options.value.length) highlighted.value = 0
})

const activeOption = computed(() => options.value[highlighted.value] ?? null)

useChosenInView(listbox, () => (activeOption.value ? optionId(activeOption.value.id) : null))

/**
 * One request at a time, each sent after the last has been answered: the server applies them in that order too, so the
 * last answer is always the newest set. A refresh waits its turn as well, so it can never bring back an older set.
 */
let queue: Promise<unknown> = Promise.resolve()
function inTurn(job: () => Promise<void>): Promise<void> {
  const run = queue.then(job, job)
  queue = run.catch(() => undefined)
  return run
}

async function startAdding() {
  adding.value = true
  query.value = ''
  highlighted.value = 0
  // Focused before anything is waited for: a phone opens its keyboard only for a focus that comes with the tap.
  await nextTick()
  input.value?.focus()
  void tagsStore.ensureLoaded().catch(() => undefined)
}

/** Closes the field. Escape gives the focus back to "Add tag"; leaving the field (a click elsewhere) leaves it where it went. */
async function cancelAdding(returnFocus: boolean) {
  if (!adding.value) return
  adding.value = false
  if (!returnFocus) return
  // The button is only there once the field is gone.
  await nextTick()
  addButton.value?.focus()
}

function change(body: { add?: string[]; remove?: string[] }): Promise<void> {
  return inTurn(async () => {
    try {
      current.value = await api<TagRef[]>('POST', `/api/pages/${props.pageId}/tags`, body)
    } catch (error) {
      notifications.post({
        type: 'error',
        code: error instanceof ApiError ? error.code : 'unknown',
      })
      // Whatever went wrong, the row may no longer match the server.
      await readAgain()
      return
    }

    // The workspace list's suggestions and counts follow: a new tag is offered next time, another page's count changed.
    void tagsStore.load().catch(() => undefined)
  })
}

async function choose(option: { id: string; name: string }) {
  await change({ add: [option.name] })
  query.value = ''
  highlighted.value = 0
  await nextTick()
  input.value?.focus()
}

async function removeTag(tag: TagRef, index: number) {
  await change({ remove: [tag.id] })
  // The button that had the focus is gone with its pill: the focus goes to the pill that took its place, or the one
  // before it, or "Add tag", unless the person has already moved it somewhere.
  await nextTick()
  if (document.activeElement && document.activeElement !== document.body) return
  const buttons = row.value?.querySelectorAll<HTMLButtonElement>('[data-remove-tag]') ?? []
  ;(buttons[index] ?? buttons[index - 1] ?? addButton.value)?.focus()
}

function onKeydown(event: KeyboardEvent) {
  // A key that confirms or moves in an input method's own list belongs to it, not to this menu.
  if (event.isComposing || event.keyCode === 229) return
  if (event.key === 'Escape') {
    event.preventDefault()
    void cancelAdding(true)
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
async function readAgain() {
  try {
    const detail = await tree.fetchPage(props.pageId)
    if (detail) current.value = detail.tags
  } catch {
    // A convenience refresh; the list already on screen stays.
  }
}

useOnTabVisible(() => void inTurn(readAgain))
</script>

<template>
  <div
    v-if="current.length > 0 || !readOnly"
    ref="row"
    class="flex flex-wrap items-center gap-space-sm"
    role="list"
    :aria-label="t('tags.label')"
  >
    <span
      v-for="(tag, index) in current"
      :key="tag.id"
      role="listitem"
      data-testid="tag-pill"
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
        data-remove-tag
        @click="removeTag(tag, index)"
      >
        <AppIcon name="close" class="size-3" />
      </button>
    </span>

    <div v-if="!readOnly && !adding" role="listitem">
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

    <div v-else-if="!readOnly" role="listitem" class="relative">
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
        @blur="cancelAdding(false)"
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
