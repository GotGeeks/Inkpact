<script setup lang="ts">
import {
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
  ListboxContent,
  ListboxFilter,
  ListboxItem,
  ListboxRoot,
} from 'reka-ui'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import AppIcon from '../components/AppIcon.vue'
import { useErrorMessage } from '../errors'
import { MIN_SEARCH_LENGTH, useSearchStore } from '../stores/search'
import { useTreeStore } from '../stores/tree'
import type { SearchSegment } from './types'

const { t, n } = useI18n()
const errorMessage = useErrorMessage()
const router = useRouter()
const search = useSearchStore()
const tree = useTreeStore()

const header = ref<HTMLElement | null>(null)
// What ListboxRoot exposes, as far as this uses it.
const listbox = ref<{ highlightFirstItem: () => void } | null>(null)

const listId = 'search-results'

const text = computed(() => search.query.trim())
const tooShort = computed(() => text.value.length < MIN_SEARCH_LENGTH)
const hasHits = computed(() => search.hits.length > 0)
/** The list on screen is the answer to the text in the field: not while the next search waits or loads. */
const fresh = computed(() => search.state === 'done' && search.searchedFor === text.value)
// Nothing found is only said for the text that was searched, not while the next search is on its way.
const nothing = computed(() => !tooShort.value && !hasHits.value && fresh.value)
const error = computed(() => {
  if (search.state !== 'error') return null
  // A search that cannot reach the server says so in its own words; anything else as the app says it everywhere.
  return search.errorCode === 'network_error'
    ? t('search.error')
    : errorMessage(search.errorCode ?? 'unknown')
})

/** The pages above each result, outermost first, as the sidebar has them: worked out once per list, not per render. */
const paths = computed(() => {
  const byId = tree.byId
  return new Map(
    search.hits.map((hit) => {
      const titles: string[] = []
      const seen = new Set<string>()
      let parent = byId.get(hit.id)?.parentId ?? null
      while (parent !== null && !seen.has(parent)) {
        seen.add(parent)
        const page = byId.get(parent)
        titles.unshift(page?.title || t('tree.untitled'))
        parent = page?.parentId ?? null
      }
      return [hit.id, titles.join(' / ')]
    }),
  )
})

const hasMatch = (segments: SearchSegment[]) => segments.some((segment) => segment.match)

// A new list starts at its first result.
watch(
  () => search.hits,
  () => {
    if (search.hits.length > 0) listbox.value?.highlightFirstItem()
  },
  { flush: 'post' },
)

function choose(id: string) {
  search.hide()
  void router.push({ name: 'page', params: { pageId: id } })
}

/**
 * Enter before the list is the answer to what was typed (the pause after the last key, or the search on its way) would
 * open a result of the text before. Instead the search runs now, and its first result opens when it is there.
 */
const enterFor = ref<string | null>(null)

function onKeydownCapture(event: KeyboardEvent) {
  if (event.key !== 'Enter' || event.isComposing || fresh.value || tooShort.value) return
  // Before the listbox's own Enter, which would open the highlighted result of the old list.
  event.preventDefault()
  event.stopPropagation()
  enterFor.value = text.value
  search.flush()
}

watch(fresh, (isFresh) => {
  if (!isFresh || enterFor.value === null) return
  const wanted = enterFor.value
  enterFor.value = null
  const first = search.hits[0]
  if (wanted === text.value && first) choose(first.id)
})

watch(text, (now) => {
  if (enterFor.value !== null && enterFor.value !== now) enterFor.value = null
})

// The last search is there when the dialog opens again, selected, so that typing replaces it.
function focusInput(event: Event) {
  event.preventDefault()
  const input = header.value?.querySelector('input')
  input?.focus()
  input?.select()
}

const markClass = 'rounded-sm bg-primary-container/30 text-inherit'
</script>

<template>
  <DialogRoot :open="search.open" @update:open="(open: boolean) => !open && search.hide()">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-40 bg-surface-canvas/70" />
      <DialogContent
        class="fixed inset-0 z-50 flex flex-col bg-surface-panel text-on-surface outline-none md:inset-auto md:start-1/2 md:top-[10dvh] md:max-h-[70dvh] md:w-[min(40rem,calc(100vw-2rem))] md:-translate-x-1/2 md:rounded-lg md:border md:border-border-subtle md:shadow-lg"
        data-testid="search-dialog"
        @open-auto-focus="focusInput"
      >
        <DialogTitle class="sr-only">{{ t('search.title') }}</DialogTitle>
        <DialogDescription class="sr-only">{{ t('search.description') }}</DialogDescription>

        <ListboxRoot ref="listbox" highlight-on-hover loop class="flex min-h-0 flex-1 flex-col">
          <div
            ref="header"
            class="flex items-center gap-space-sm border-b border-border-subtle px-space-md"
            @keydown.capture="onKeydownCapture"
          >
            <AppIcon name="search" class="size-5 text-on-surface-variant" />
            <ListboxFilter
              :model-value="search.query"
              role="combobox"
              :aria-label="t('search.inputLabel')"
              :placeholder="t('search.placeholder')"
              :aria-expanded="hasHits"
              :aria-controls="listId"
              v-bind="hasHits ? {} : { 'aria-activedescendant': undefined }"
              aria-autocomplete="list"
              autocomplete="off"
              autocapitalize="off"
              spellcheck="false"
              enterkeyhint="search"
              class="min-h-14 min-w-0 flex-1 bg-transparent text-body-lg text-on-surface outline-none placeholder:text-on-surface-variant"
              data-testid="search-input"
              @update:model-value="search.setQuery"
            />
            <button
              type="button"
              :aria-label="t('search.close')"
              class="flex size-11 shrink-0 items-center justify-center rounded text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary md:size-9"
              @click="search.hide()"
            >
              <AppIcon name="close" class="size-5" />
            </button>
          </div>

          <div class="min-h-0 flex-1 overflow-y-auto p-space-sm">
            <!-- Always there, also empty, so that the field's aria-controls points at it. -->
            <ListboxContent
              :id="listId"
              :aria-label="t('search.results')"
              :aria-busy="hasHits && !fresh"
              class="flex flex-col"
              data-testid="search-results"
            >
              <ListboxItem
                v-for="hit in search.hits"
                :key="hit.id"
                :value="hit.id"
                :data-page-id="hit.id"
                class="flex min-h-11 cursor-pointer flex-col gap-space-xs rounded px-space-md py-space-sm text-start text-on-surface outline-none data-highlighted:bg-surface-container-high"
                @select="choose(hit.id)"
              >
                <span class="text-body-lg">
                  <template v-if="hit.title.length > 0">
                    <template v-for="(segment, i) in hit.title" :key="i">
                      <mark v-if="segment.match" :class="markClass">{{ segment.text }}</mark>
                      <template v-else>{{ segment.text }}</template>
                    </template>
                  </template>
                  <span v-else class="text-on-surface-variant italic">{{
                    t('tree.untitled')
                  }}</span>
                </span>
                <span
                  v-if="paths.get(hit.id)"
                  class="truncate text-label-md text-on-surface-variant"
                  data-testid="search-path"
                  >{{ paths.get(hit.id) }}</span
                >
                <span
                  v-if="hasMatch(hit.snippet)"
                  class="line-clamp-2 text-body-sm text-on-surface-variant"
                  data-testid="search-snippet"
                >
                  <template v-for="(segment, i) in hit.snippet" :key="i">
                    <mark v-if="segment.match" :class="markClass">{{ segment.text }}</mark>
                    <template v-else>{{ segment.text }}</template>
                  </template>
                </span>
              </ListboxItem>
            </ListboxContent>

            <p v-if="error" class="p-space-md text-body-sm text-error" data-testid="search-error">
              {{ error }}
            </p>
            <p
              v-else-if="tooShort"
              class="p-space-md text-body-sm text-on-surface-variant"
              data-testid="search-hint"
            >
              {{ t('search.hint', { count: n(MIN_SEARCH_LENGTH) }, MIN_SEARCH_LENGTH) }}
            </p>
            <p
              v-else-if="nothing"
              class="p-space-md text-body-sm text-on-surface-variant"
              data-testid="search-empty"
            >
              {{ t('search.empty', { query: text }) }}
            </p>
            <p
              v-if="hasHits && search.more"
              class="p-space-md text-body-sm text-on-surface-variant"
              data-testid="search-more"
            >
              {{ t('search.more', { count: n(search.hits.length) }, search.hits.length) }}
            </p>
          </div>
        </ListboxRoot>

        <!-- What a screen reader hears when the list changes. -->
        <p role="status" class="sr-only">
          <template v-if="search.state === 'loading'">{{ t('search.searching') }}</template>
          <template v-else-if="error">{{ error }}</template>
          <template v-else-if="hasHits">{{
            t('search.count', { count: n(search.hits.length) }, search.hits.length)
          }}</template>
          <template v-else-if="nothing">{{ t('search.empty', { query: text }) }}</template>
        </p>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
