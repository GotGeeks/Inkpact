<script setup lang="ts">
import {
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import AppIcon from '../components/AppIcon.vue'
import { MIN_SEARCH_LENGTH, useSearchStore } from '../stores/search'
import { useTreeStore } from '../stores/tree'
import { ancestorIds } from '../tree/treeModel'
import type { SearchSegment } from './types'

const { t, n } = useI18n()
const router = useRouter()
const search = useSearchStore()
const tree = useTreeStore()

const input = ref<HTMLInputElement | null>(null)
const active = ref(0)

const listId = 'search-results'
const optionId = (index: number) => `search-option-${index}`

const text = computed(() => search.query.trim())
const tooShort = computed(() => text.value.length < MIN_SEARCH_LENGTH)
const hasHits = computed(() => search.hits.length > 0)
// Nothing found is only said for the text that was searched, not while the next search is on its way.
const nothing = computed(
  () =>
    !tooShort.value &&
    !hasHits.value &&
    search.state === 'done' &&
    search.searchedFor === text.value,
)

// A new list starts at its first result.
watch(
  () => search.hits,
  () => {
    active.value = 0
  },
)

watch(active, async (index) => {
  await nextTick()
  document.getElementById(optionId(index))?.scrollIntoView?.({ block: 'nearest' })
})

/** The pages above a page, outermost first, as the sidebar has them. */
function pathOf(id: string): string {
  return ancestorIds(tree.pages, id)
    .reverse()
    .map((ancestor) => tree.byId.get(ancestor)?.title || t('tree.untitled'))
    .join(' / ')
}

const hasMatch = (segments: SearchSegment[]) => segments.some((segment) => segment.match)

function choose(id: string) {
  search.hide()
  void router.push({ name: 'page', params: { pageId: id } })
}

function onKeydown(event: KeyboardEvent) {
  const count = search.hits.length
  if (event.key === 'ArrowDown' && count > 0) {
    event.preventDefault()
    active.value = (active.value + 1) % count
  } else if (event.key === 'ArrowUp' && count > 0) {
    event.preventDefault()
    active.value = (active.value - 1 + count) % count
  } else if (event.key === 'Enter') {
    const hit = search.hits[active.value]
    if (hit && !event.isComposing) {
      event.preventDefault()
      choose(hit.id)
    }
  }
}

// The last search is there when the dialog opens again, selected, so that typing replaces it.
function focusInput(event: Event) {
  event.preventDefault()
  input.value?.focus()
  input.value?.select()
}

const option =
  'flex cursor-pointer flex-col gap-space-xs rounded px-space-md py-space-sm text-start text-on-surface'
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

        <div class="flex items-center gap-space-sm border-b border-border-subtle px-space-md">
          <AppIcon name="search" class="size-5 text-on-surface-variant" />
          <input
            ref="input"
            :value="search.query"
            type="text"
            role="combobox"
            :aria-label="t('search.inputLabel')"
            :placeholder="t('search.placeholder')"
            :aria-expanded="hasHits"
            :aria-controls="listId"
            :aria-activedescendant="hasHits ? optionId(active) : undefined"
            aria-autocomplete="list"
            autocomplete="off"
            autocapitalize="off"
            spellcheck="false"
            enterkeyhint="search"
            class="min-h-14 min-w-0 flex-1 bg-transparent text-body-lg text-on-surface outline-none placeholder:text-on-surface-variant"
            data-testid="search-input"
            @input="search.setQuery(($event.target as HTMLInputElement).value)"
            @keydown="onKeydown"
          />
          <button
            type="button"
            :aria-label="t('search.close')"
            class="flex size-11 shrink-0 items-center justify-center rounded text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary-container md:size-9"
            @click="search.hide()"
          >
            <AppIcon name="close" class="size-5" />
          </button>
        </div>

        <div class="min-h-0 flex-1 overflow-y-auto p-space-sm">
          <ul
            v-if="hasHits"
            :id="listId"
            role="listbox"
            :aria-label="t('search.results')"
            :aria-busy="search.state === 'loading'"
            class="flex flex-col"
            data-testid="search-results"
          >
            <li
              v-for="(hit, index) in search.hits"
              :id="optionId(index)"
              :key="hit.id"
              role="option"
              :aria-selected="index === active"
              :data-page-id="hit.id"
              :class="[option, index === active ? 'bg-surface-container-high' : '']"
              @click="choose(hit.id)"
              @mousemove="active = index"
            >
              <span class="text-body-lg">
                <template v-if="hit.title.length > 0">
                  <template v-for="(segment, i) in hit.title" :key="i">
                    <mark v-if="segment.match" :class="markClass">{{ segment.text }}</mark>
                    <template v-else>{{ segment.text }}</template>
                  </template>
                </template>
                <span v-else class="text-on-surface-variant italic">{{ t('tree.untitled') }}</span>
              </span>
              <span
                v-if="pathOf(hit.id)"
                class="truncate text-label-md text-on-surface-variant"
                data-testid="search-path"
                >{{ pathOf(hit.id) }}</span
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
            </li>
          </ul>

          <p
            v-if="search.state === 'error'"
            class="p-space-md text-body-sm text-error"
            data-testid="search-error"
          >
            {{ t('search.error') }}
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

        <!-- What a screen reader hears when the list changes. -->
        <p role="status" class="sr-only">
          <template v-if="search.state === 'loading'">{{ t('search.searching') }}</template>
          <template v-else-if="hasHits">{{
            t('search.count', { count: n(search.hits.length) }, search.hits.length)
          }}</template>
          <template v-else-if="nothing">{{ t('search.empty', { query: text }) }}</template>
        </p>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
