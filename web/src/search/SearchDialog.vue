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
  TabsContent,
  TabsList,
  TabsRoot,
  TabsTrigger,
} from 'reka-ui'
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import AppIcon from '../components/AppIcon.vue'
import { useErrorMessage } from '../errors'
import { useCurrentPageStore } from '../stores/currentPage'
import { useFavoritesStore } from '../stores/favorites'
import { useRecentStore } from '../stores/recent'
import { useSearchStore, MIN_SEARCH_LENGTH } from '../stores/search'
import { useTagsStore } from '../stores/tags'
import { useTreeStore } from '../stores/tree'
import { availableCommands, findCommands, type PaletteCommandId } from '../palette/commands'
import { findTags, MAX_TAG_SUGGESTIONS } from '../tags/tagSearch'
import type { SearchSegment } from './types'

const emit = defineEmits<{ command: [id: PaletteCommandId] }>()

const { t, n } = useI18n()
const errorMessage = useErrorMessage()
const router = useRouter()
const search = useSearchStore()
const tree = useTreeStore()
const tags = useTagsStore()
const favorites = useFavoritesStore()
const recent = useRecentStore()
const current = useCurrentPageStore()

const helpBackButton = ref<HTMLButtonElement | null>(null)
const pagesListbox = ref<{ highlightFirstItem: () => void } | null>(null)
const tagsListbox = ref<{ highlightFirstItem: () => void } | null>(null)
const commandsListbox = ref<{ highlightFirstItem: () => void } | null>(null)
const localQueries = ref({ tags: '', commands: '' })
const pageListsLoading = ref(false)
const favoritesReady = ref(false)
const recentReady = ref(false)
const treeLoadFailed = ref(false)
const pageListsLoaded = ref(false)
const tagsLoadedForSession = ref(false)
let pageListsGeneration = 0
let pageListsRequest: Promise<void> | null = null

const isMac = /Mac|iPhone|iPad/.test(globalThis.navigator?.userAgent ?? '')
const modifier = computed(() => (isMac ? '⌘' : 'Ctrl'))
const commandModifier = computed(() => (isMac ? '⌘+Option' : 'Ctrl+Alt'))
const scopes = ['pages', 'tags', 'commands'] as const
const commandTranslations = computed<Record<string, string>>(() => ({
  'palette.commands.newPage': t('palette.commands.newPage'),
  'palette.commands.favoriteAdd': t('palette.commands.favoriteAdd'),
  'palette.commands.favoriteRemove': t('palette.commands.favoriteRemove'),
  'palette.commands.home': t('palette.commands.home'),
  'palette.commands.tags': t('palette.commands.tags'),
  'palette.commands.trash': t('palette.commands.trash'),
  'palette.commands.import': t('palette.commands.import'),
  'palette.commands.shortcuts': t('palette.commands.shortcuts'),
  'palette.commandDescriptions.newPage': t('palette.commandDescriptions.newPage'),
  'palette.commandDescriptions.favorite': t('palette.commandDescriptions.favorite'),
  'palette.commandDescriptions.home': t('palette.commandDescriptions.home'),
  'palette.commandDescriptions.tags': t('palette.commandDescriptions.tags'),
  'palette.commandDescriptions.trash': t('palette.commandDescriptions.trash'),
  'palette.commandDescriptions.import': t('palette.commandDescriptions.import'),
  'palette.commandDescriptions.shortcuts': t('palette.commandDescriptions.shortcuts'),
}))
function translateCommand(key: string): string {
  return commandTranslations.value[key] ?? t(key)
}

const tabLabels = computed(() => ({
  pages: t('palette.tabs.pages'),
  tags: t('palette.tabs.tags'),
  commands: t('palette.tabs.commands'),
}))
const text = computed(() => search.query.trim())
const scopeText = computed(() =>
  search.scope === 'pages' ? text.value : localQueries.value[search.scope].trim(),
)
const tooShort = computed(() => text.value.length < MIN_SEARCH_LENGTH)
const hasHits = computed(() => search.hits.length > 0)
const fresh = computed(() => search.state === 'done' && search.searchedFor === text.value)
const nothing = computed(() => !tooShort.value && !hasHits.value && fresh.value)
const error = computed(() => {
  if (search.state !== 'error') return null
  return search.errorCode === 'network_error'
    ? t('search.error')
    : errorMessage(search.errorCode ?? 'unknown')
})

const quickFavorites = computed(() =>
  favoritesReady.value && favorites.loadError === null
    ? favorites.ids
        .map((id) => tree.byId.get(id))
        .filter((page) => page !== undefined)
        .slice(0, 5)
    : [],
)
const quickRecent = computed(() => {
  const favoriteIds = new Set(quickFavorites.value.map((page) => page.id))
  return recentReady.value && recent.loadError === null
    ? recent.items
        .filter((item) => !favoriteIds.has(item.pageId))
        .map((item) => tree.byId.get(item.pageId))
        .filter((page) => page !== undefined)
        .slice(0, 5)
    : []
})
const quickPagesEmpty = computed(
  () =>
    favoritesReady.value &&
    recentReady.value &&
    favorites.loadError === null &&
    recent.loadError === null &&
    !treeLoadFailed.value &&
    quickFavorites.value.length === 0 &&
    quickRecent.value.length === 0,
)

const matchingTags = computed(() =>
  findTags(tags.items, scopeText.value, new Set(), tags.items.length),
)
const visibleTags = computed(() => matchingTags.value.slice(0, MAX_TAG_SUGGESTIONS))
const tagsHaveMore = computed(() => matchingTags.value.length > MAX_TAG_SUGGESTIONS)
const commands = computed(() => {
  const currentId = current.id
  const hasLiveCurrentPage =
    currentId !== null && !current.missing && !current.inTrash && tree.byId.has(currentId)
  return findCommands(
    availableCommands({
      canEdit: tree.canEdit,
      hasWorkspace: tree.workspace !== null,
      hasLiveCurrentPage,
      favoritesReady: favorites.loaded && favorites.loadError === null,
      currentPageIsFavorite: currentId !== null && favorites.has(currentId),
    }),
    scopeText.value,
    translateCommand,
  )
})

/** The pages above a page, outermost first, as the sidebar has them. */
function pagePath(id: string): string {
  const titles: string[] = []
  const seen = new Set<string>()
  let parent = tree.byId.get(id)?.parentId ?? null
  while (parent !== null && !seen.has(parent)) {
    seen.add(parent)
    const page = tree.byId.get(parent)
    titles.unshift(page?.title || t('tree.untitled'))
    parent = page?.parentId ?? null
  }
  return titles.join(' / ')
}

const paths = computed(() => new Map(search.hits.map((hit) => [hit.id, pagePath(hit.id)])))
const hasMatch = (segments: SearchSegment[]) => segments.some((segment) => segment.match)

function missingListPageIds(): boolean {
  return (
    (favoritesReady.value &&
      favorites.loadError === null &&
      favorites.ids.some((id) => !tree.byId.has(id))) ||
    (recentReady.value &&
      recent.loadError === null &&
      recent.items.some((item) => !tree.byId.has(item.pageId)))
  )
}

function currentPageListRequest(generation: number, workspaceId: string | null): boolean {
  return search.open && generation === pageListsGeneration && tree.workspace?.id === workspaceId
}

async function loadPageLists(force = false): Promise<void> {
  if (pageListsRequest) return pageListsRequest
  if (pageListsLoaded.value && !force) return

  const generation = pageListsGeneration
  const workspaceId = tree.workspace?.id ?? null
  pageListsLoading.value = true
  treeLoadFailed.value = false
  favoritesReady.value = false
  recentReady.value = false
  const request = (async () => {
    const favoriteRequest = favorites.load().then((loaded) => {
      if (currentPageListRequest(generation, workspaceId)) favoritesReady.value = loaded
    })
    const recentRequest = recent.load().then((loaded) => {
      if (currentPageListRequest(generation, workspaceId)) recentReady.value = loaded
    })
    await Promise.all([favoriteRequest, recentRequest])
    if (!currentPageListRequest(generation, workspaceId)) return

    if (missingListPageIds()) {
      try {
        const refreshed = await tree.load()
        if (!currentPageListRequest(generation, workspaceId)) return
        if (!refreshed && missingListPageIds()) treeLoadFailed.value = true
      } catch {
        if (currentPageListRequest(generation, workspaceId)) treeLoadFailed.value = true
      }
    }
    if (currentPageListRequest(generation, workspaceId)) pageListsLoaded.value = true
  })()
  pageListsRequest = request
  try {
    await request
  } finally {
    if (pageListsRequest === request) {
      pageListsRequest = null
      pageListsLoading.value = false
    }
  }
}

function retryPageLists() {
  pageListsLoaded.value = false
  void loadPageLists(true)
}

function loadTags() {
  if (tagsLoadedForSession.value) return
  tagsLoadedForSession.value = true
  void tags.load().catch(() => undefined)
}

function retryTags() {
  tagsLoadedForSession.value = true
  void tags.load().catch(() => undefined)
}

watch(
  () => [search.open, search.scope] as const,
  ([open, scope], previous) => {
    const wasOpen = previous?.[0] ?? false
    if (open !== wasOpen) {
      pageListsGeneration++
      pageListsRequest = null
      pageListsLoading.value = false
      pageListsLoaded.value = false
      favoritesReady.value = false
      recentReady.value = false
      treeLoadFailed.value = false
      tagsLoadedForSession.value = false
      if (open) localQueries.value = { tags: '', commands: '' }
    }

    if (!open) return
    if (scope === 'pages') void loadPageLists()
    else if (scope === 'tags') loadTags()
  },
  { immediate: true, flush: 'sync' },
)

watch(
  () => search.hits,
  () => {
    if (search.hits.length > 0) pagesListbox.value?.highlightFirstItem()
  },
  { flush: 'post' },
)
watch(visibleTags, () => {
  if (visibleTags.value.length > 0) tagsListbox.value?.highlightFirstItem()
})
watch(commands, () => {
  if (commands.value.length > 0) commandsListbox.value?.highlightFirstItem()
})

function choose(id: string) {
  search.hide()
  void router.push({ name: 'page', params: { pageId: id } })
}

function chooseTag(id: string) {
  search.hide()
  void router.push({ name: 'tag', params: { tagId: id } })
}

function chooseCommand(id: PaletteCommandId) {
  emit('command', id)
}

function setScope(value: unknown) {
  if (typeof value !== 'string' || !scopes.includes(value as (typeof scopes)[number])) return
  search.setScope(value as (typeof scopes)[number])
}

function onKeydownCapture(event: KeyboardEvent) {
  if (
    search.scope !== 'pages' ||
    event.key !== 'Enter' ||
    event.isComposing ||
    fresh.value ||
    tooShort.value
  )
    return
  event.preventDefault()
  event.stopPropagation()
  enterFor.value = text.value
  search.flush()
}

const enterFor = ref<string | null>(null)
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

function focusInput(event: Event) {
  event.preventDefault()
  const input = activeInput()
  input?.focus()
  if (input instanceof HTMLInputElement) input.select()
}

function changeLocalQuery(value: string) {
  if (search.scope !== 'pages') localQueries.value[search.scope] = value
}

watch(
  () => search.helpOpen,
  async (open) => {
    if (!search.open) return
    await nextTick()
    if (open) helpBackButton.value?.focus()
    else focusActiveInput()
  },
)

function focusActiveInput() {
  activeInput()?.focus()
}

function activeInput(): HTMLElement | null {
  const testId = search.scope === 'pages' ? 'search-input' : `palette-input-${search.scope}`
  return document.querySelector<HTMLElement>(`[data-testid="${testId}"]`)
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
        <DialogTitle class="sr-only">{{
          search.helpOpen ? t('palette.shortcutHelpTitle') : t('palette.title')
        }}</DialogTitle>
        <DialogDescription class="sr-only">{{
          search.helpOpen ? t('palette.shortcutHelpDescription') : t('palette.description')
        }}</DialogDescription>

        <template v-if="search.helpOpen">
          <div class="flex items-center justify-between border-b border-border-subtle p-space-md">
            <h2 class="text-headline-sm">{{ t('palette.shortcutHelpTitle') }}</h2>
            <button
              ref="helpBackButton"
              type="button"
              class="min-h-11 rounded px-space-md text-label-md text-primary hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary"
              @click="search.hideHelp()"
            >
              {{ t('palette.shortcutHelpBack') }}
            </button>
          </div>
          <ul class="flex flex-col gap-space-sm overflow-y-auto p-space-md">
            <li class="flex items-center justify-between gap-space-md">
              <span>{{ t('palette.shortcutOpenPages') }}</span>
              <kbd class="shrink-0 rounded bg-surface-container-high px-space-sm text-label-xs">{{
                t('palette.keys.openPages', { modifier })
              }}</kbd>
            </li>
            <li class="flex items-center justify-between gap-space-md">
              <span>{{ t('palette.shortcutOpenCommands') }}</span>
              <kbd class="shrink-0 rounded bg-surface-container-high px-space-sm text-label-xs">{{
                t('palette.keys.openCommands', { commandModifier })
              }}</kbd>
            </li>
            <li class="flex items-center justify-between gap-space-md">
              <span>{{ t('palette.shortcutNewPage') }}</span>
              <kbd class="shrink-0 rounded bg-surface-container-high px-space-sm text-label-xs">{{
                t('palette.keys.newPage', { commandModifier })
              }}</kbd>
            </li>
            <li class="flex items-center justify-between gap-space-md">
              <span>{{ t('palette.shortcutTreeRename') }}</span>
              <kbd class="shrink-0 rounded bg-surface-container-high px-space-sm text-label-xs">{{
                t('palette.keys.treeRename')
              }}</kbd>
            </li>
            <li class="flex items-center justify-between gap-space-md">
              <span>{{ t('palette.shortcutTreeDelete') }}</span>
              <kbd class="shrink-0 rounded bg-surface-container-high px-space-sm text-label-xs">{{
                t('palette.keys.treeDelete')
              }}</kbd>
            </li>
            <li class="flex items-center justify-between gap-space-md">
              <span>{{ t('palette.shortcutTreeMove') }}</span>
              <kbd class="shrink-0 rounded bg-surface-container-high px-space-sm text-label-xs">{{
                t('palette.keys.treeMove')
              }}</kbd>
            </li>
            <li class="flex items-center justify-between gap-space-md">
              <span>{{ t('palette.shortcutBlockMove') }}</span>
              <kbd class="shrink-0 rounded bg-surface-container-high px-space-sm text-label-xs">{{
                t('palette.keys.blockMove')
              }}</kbd>
            </li>
          </ul>
        </template>

        <template v-else>
          <TabsRoot
            :model-value="search.scope"
            activation-mode="automatic"
            class="flex min-h-0 flex-1 flex-col"
            @update:model-value="setScope"
          >
            <TabsList
              :aria-label="t('palette.tabsLabel')"
              class="flex border-b border-border-subtle px-space-sm"
              data-testid="palette-tabs"
            >
              <TabsTrigger
                v-for="scope in scopes"
                :key="scope"
                :value="scope"
                :data-testid="`palette-tab-${scope}`"
                class="min-h-11 flex-1 border-b-2 border-transparent px-space-sm text-label-md text-on-surface-variant hover:bg-surface-elevated data-[state=active]:border-primary data-[state=active]:text-on-surface focus-visible:outline-2 focus-visible:outline-primary"
              >
                {{ tabLabels[scope] }}
              </TabsTrigger>
            </TabsList>

            <TabsContent value="pages" class="flex min-h-0 flex-1 flex-col outline-none">
              <ListboxRoot
                ref="pagesListbox"
                highlight-on-hover
                loop
                class="flex min-h-0 flex-1 flex-col"
                @keydown.capture="onKeydownCapture"
              >
                <div
                  class="flex items-center gap-space-sm border-b border-border-subtle px-space-md"
                >
                  <AppIcon name="search" class="size-5 text-on-surface-variant" />
                  <ListboxFilter
                    :model-value="search.query"
                    role="combobox"
                    :aria-label="t('palette.inputLabels.pages')"
                    :placeholder="t('palette.placeholders.pages')"
                    :aria-expanded="
                      hasHits ||
                      (text.length === 0 && quickFavorites.length + quickRecent.length > 0)
                    "
                    aria-controls="palette-pages-results"
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
                    :aria-label="t('palette.close')"
                    class="flex size-11 shrink-0 items-center justify-center rounded text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary md:size-9"
                    @click="search.hide()"
                  >
                    <AppIcon name="close" class="size-5" />
                  </button>
                </div>

                <div class="min-h-0 flex-1 overflow-y-auto p-space-sm">
                  <ListboxContent
                    id="palette-pages-results"
                    :aria-label="t('search.results')"
                    :aria-busy="(hasHits && !fresh) || pageListsLoading"
                    class="flex flex-col"
                    data-testid="search-results"
                  >
                    <template v-if="text.length === 0">
                      <section
                        v-if="
                          quickFavorites.length ||
                          (favoritesReady && recentReady && quickRecent.length > 0)
                        "
                        aria-labelledby="quick-favorites-heading"
                      >
                        <h2
                          id="quick-favorites-heading"
                          class="px-space-md py-space-xs text-label-xs text-on-surface-variant"
                        >
                          {{ t('palette.favorites') }}
                        </h2>
                        <ListboxItem
                          v-for="page in quickFavorites"
                          :key="`favorite-${page.id}`"
                          :value="`favorite:${page.id}`"
                          :data-page-id="page.id"
                          class="flex min-h-11 cursor-pointer flex-col gap-space-xs rounded px-space-md py-space-sm text-start text-on-surface outline-none data-highlighted:bg-surface-container-high"
                          @select="choose(page.id)"
                        >
                          <span class="text-body-lg">{{ page.title || t('tree.untitled') }}</span>
                          <span
                            v-if="pagePath(page.id)"
                            class="truncate text-label-md text-on-surface-variant"
                          >
                            {{ pagePath(page.id) }}
                          </span>
                        </ListboxItem>
                        <p
                          v-if="favoritesReady && quickFavorites.length === 0"
                          class="px-space-md py-space-xs text-body-sm text-on-surface-variant"
                        >
                          {{ t('palette.noFavorites') }}
                        </p>
                      </section>
                      <section
                        v-if="
                          quickRecent.length ||
                          (recentReady && favoritesReady && quickFavorites.length > 0)
                        "
                        aria-labelledby="quick-recent-heading"
                      >
                        <h2
                          id="quick-recent-heading"
                          class="px-space-md py-space-xs text-label-xs text-on-surface-variant"
                        >
                          {{ t('palette.recent') }}
                        </h2>
                        <ListboxItem
                          v-for="page in quickRecent"
                          :key="`recent-${page.id}`"
                          :value="`recent:${page.id}`"
                          :data-page-id="page.id"
                          class="flex min-h-11 cursor-pointer flex-col gap-space-xs rounded px-space-md py-space-sm text-start text-on-surface outline-none data-highlighted:bg-surface-container-high"
                          @select="choose(page.id)"
                        >
                          <span class="text-body-lg">{{ page.title || t('tree.untitled') }}</span>
                          <span
                            v-if="pagePath(page.id)"
                            class="truncate text-label-md text-on-surface-variant"
                          >
                            {{ pagePath(page.id) }}
                          </span>
                        </ListboxItem>
                        <p
                          v-if="recentReady && quickRecent.length === 0"
                          class="px-space-md py-space-xs text-body-sm text-on-surface-variant"
                        >
                          {{ t('palette.noRecent') }}
                        </p>
                      </section>
                    </template>

                    <ListboxItem
                      v-for="hit in text.length >= MIN_SEARCH_LENGTH ? search.hits : []"
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

                  <p
                    v-if="pageListsLoading && text.length === 0"
                    role="status"
                    class="p-space-md text-body-sm text-on-surface-variant"
                  >
                    {{ t('palette.pagesLoading') }}
                  </p>
                  <div
                    v-if="favorites.loadError"
                    class="flex items-center justify-between gap-space-md p-space-md text-body-sm text-error"
                    role="alert"
                  >
                    <span
                      >{{ t('palette.favoritesFailed') }}
                      {{ errorMessage(favorites.loadError) }}</span
                    >
                    <button class="shrink-0 underline" type="button" @click="retryPageLists">
                      {{ t('palette.retry') }}
                    </button>
                  </div>
                  <div
                    v-if="recent.loadError"
                    class="flex items-center justify-between gap-space-md p-space-md text-body-sm text-error"
                    role="alert"
                  >
                    <span
                      >{{ t('palette.recentFailed') }} {{ errorMessage(recent.loadError) }}</span
                    >
                    <button class="shrink-0 underline" type="button" @click="retryPageLists">
                      {{ t('palette.retry') }}
                    </button>
                  </div>
                  <div
                    v-if="treeLoadFailed"
                    class="flex items-center justify-between gap-space-md p-space-md text-body-sm text-error"
                    role="alert"
                  >
                    <span>{{ t('palette.treeFailed') }}</span>
                    <button class="shrink-0 underline" type="button" @click="retryPageLists">
                      {{ t('palette.retry') }}
                    </button>
                  </div>
                  <p
                    v-if="text.length === 0 && quickPagesEmpty"
                    class="p-space-md text-body-sm text-on-surface-variant"
                    data-testid="palette-pages-empty"
                  >
                    {{ t('palette.noPages') }}
                  </p>
                  <template v-if="text.length >= MIN_SEARCH_LENGTH">
                    <p
                      v-if="error"
                      class="p-space-md text-body-sm text-error"
                      data-testid="search-error"
                      role="alert"
                    >
                      {{ error }}
                    </p>
                    <p
                      v-else-if="nothing"
                      class="p-space-md text-body-sm text-on-surface-variant"
                      data-testid="search-empty"
                    >
                      {{ t('search.empty', { query: text }) }}
                    </p>
                    <p
                      v-if="hasHits && fresh && search.more"
                      class="p-space-md text-body-sm text-on-surface-variant"
                      data-testid="search-more"
                    >
                      {{ t('search.more', { count: n(search.hits.length) }, search.hits.length) }}
                    </p>
                  </template>
                  <p
                    v-if="text.length < MIN_SEARCH_LENGTH"
                    class="p-space-md text-body-sm text-on-surface-variant"
                    data-testid="search-hint"
                  >
                    {{ t('search.hint', { count: n(MIN_SEARCH_LENGTH) }, MIN_SEARCH_LENGTH) }}
                  </p>
                </div>
                <p role="status" class="sr-only">
                  <template v-if="search.state === 'loading'">{{ t('search.searching') }}</template>
                  <template v-else-if="error">{{ error }}</template>
                  <template v-else-if="hasHits && fresh">{{
                    t('search.count', { count: n(search.hits.length) }, search.hits.length)
                  }}</template>
                  <template v-else-if="nothing">{{ t('search.empty', { query: text }) }}</template>
                </p>
              </ListboxRoot>
            </TabsContent>

            <TabsContent value="tags" class="flex min-h-0 flex-1 flex-col outline-none">
              <ListboxRoot
                ref="tagsListbox"
                highlight-on-hover
                loop
                class="flex min-h-0 flex-1 flex-col"
              >
                <div
                  class="flex items-center gap-space-sm border-b border-border-subtle px-space-md"
                >
                  <AppIcon name="tag" class="size-5 text-on-surface-variant" />
                  <ListboxFilter
                    :model-value="localQueries.tags"
                    role="combobox"
                    :aria-label="t('palette.inputLabels.tags')"
                    :placeholder="t('palette.placeholders.tags')"
                    :aria-expanded="visibleTags.length > 0"
                    aria-controls="palette-tags-results"
                    aria-autocomplete="list"
                    autocomplete="off"
                    autocapitalize="off"
                    spellcheck="false"
                    class="min-h-14 min-w-0 flex-1 bg-transparent text-body-lg text-on-surface outline-none placeholder:text-on-surface-variant"
                    data-testid="palette-input-tags"
                    @update:model-value="changeLocalQuery"
                  />
                  <button
                    type="button"
                    :aria-label="t('palette.close')"
                    class="flex size-11 shrink-0 items-center justify-center rounded text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary md:size-9"
                    @click="search.hide()"
                  >
                    <AppIcon name="close" class="size-5" />
                  </button>
                </div>
                <div class="min-h-0 flex-1 overflow-y-auto p-space-sm">
                  <ListboxContent
                    id="palette-tags-results"
                    :aria-label="t('palette.tabs.tags')"
                    :aria-busy="!tags.loaded && !tags.loadError"
                    class="flex flex-col"
                  >
                    <ListboxItem
                      v-for="tag in visibleTags"
                      :key="tag.id"
                      :value="tag.id"
                      class="flex min-h-11 cursor-pointer items-center justify-between gap-space-md rounded px-space-md py-space-sm text-start text-on-surface outline-none data-highlighted:bg-surface-container-high"
                      @select="chooseTag(tag.id)"
                    >
                      <span>{{ tag.name }}</span>
                      <span class="text-label-xs text-on-surface-variant">{{
                        t('tags.pageCount', { count: n(tag.pageCount) }, tag.pageCount)
                      }}</span>
                    </ListboxItem>
                  </ListboxContent>
                  <p
                    v-if="!tags.loaded && !tags.loadError"
                    role="status"
                    class="p-space-md text-body-sm"
                  >
                    {{ t('palette.tagsLoading') }}
                  </p>
                  <div
                    v-if="tags.loadError"
                    role="alert"
                    class="flex items-center justify-between gap-space-md p-space-md text-body-sm text-error"
                  >
                    <span>{{ t('palette.tagsFailed') }} {{ errorMessage(tags.loadError) }}</span>
                    <button class="shrink-0 underline" type="button" @click="retryTags">
                      {{ t('palette.retry') }}
                    </button>
                  </div>
                  <p
                    v-else-if="tags.loaded && visibleTags.length === 0"
                    class="p-space-md text-body-sm text-on-surface-variant"
                    data-testid="palette-tags-empty"
                  >
                    {{
                      scopeText
                        ? t('palette.noTagMatches', { query: scopeText })
                        : t('palette.noTags')
                    }}
                  </p>
                  <p
                    v-if="tagsHaveMore"
                    class="p-space-md text-body-sm text-on-surface-variant"
                    data-testid="palette-tags-more"
                  >
                    {{
                      t('palette.moreTags', { count: n(visibleTags.length) }, visibleTags.length)
                    }}
                  </p>
                </div>
              </ListboxRoot>
            </TabsContent>

            <TabsContent value="commands" class="flex min-h-0 flex-1 flex-col outline-none">
              <ListboxRoot
                ref="commandsListbox"
                highlight-on-hover
                loop
                class="flex min-h-0 flex-1 flex-col"
              >
                <div
                  class="flex items-center gap-space-sm border-b border-border-subtle px-space-md"
                >
                  <AppIcon name="search" class="size-5 text-on-surface-variant" />
                  <ListboxFilter
                    :model-value="localQueries.commands"
                    role="combobox"
                    :aria-label="t('palette.inputLabels.commands')"
                    :placeholder="t('palette.placeholders.commands')"
                    :aria-expanded="commands.length > 0"
                    aria-controls="palette-commands-results"
                    aria-autocomplete="list"
                    autocomplete="off"
                    autocapitalize="off"
                    spellcheck="false"
                    class="min-h-14 min-w-0 flex-1 bg-transparent text-body-lg text-on-surface outline-none placeholder:text-on-surface-variant"
                    data-testid="palette-input-commands"
                    @update:model-value="changeLocalQuery"
                  />
                  <button
                    type="button"
                    :aria-label="t('palette.close')"
                    class="flex size-11 shrink-0 items-center justify-center rounded text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary md:size-9"
                    @click="search.hide()"
                  >
                    <AppIcon name="close" class="size-5" />
                  </button>
                </div>
                <div class="min-h-0 flex-1 overflow-y-auto p-space-sm">
                  <ListboxContent
                    id="palette-commands-results"
                    :aria-label="t('palette.tabs.commands')"
                    class="flex flex-col"
                  >
                    <ListboxItem
                      v-for="command in commands"
                      :key="command.id"
                      :value="command.id"
                      class="flex min-h-11 cursor-pointer items-center justify-between gap-space-md rounded px-space-md py-space-sm text-start text-on-surface outline-none data-highlighted:bg-surface-container-high"
                      @select="chooseCommand(command.id)"
                    >
                      <span class="min-w-0 flex-1">
                        <span class="block text-body-md">{{
                          translateCommand(command.labelKey)
                        }}</span>
                        <span class="block text-label-xs text-on-surface-variant">{{
                          translateCommand(command.descriptionKey)
                        }}</span>
                      </span>
                      <kbd
                        v-if="command.shortcut === 'newPage'"
                        class="shrink-0 rounded bg-surface-container-high px-space-sm text-label-xs"
                      >
                        {{ t('palette.keys.newPage', { commandModifier }) }}
                      </kbd>
                    </ListboxItem>
                  </ListboxContent>
                  <p
                    v-if="commands.length === 0"
                    class="p-space-md text-body-sm text-on-surface-variant"
                    data-testid="palette-commands-empty"
                  >
                    {{ t('palette.noCommands', { query: scopeText }) }}
                  </p>
                  <button
                    type="button"
                    class="mt-space-sm min-h-11 rounded px-space-md text-label-md text-primary hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary"
                    data-testid="palette-shortcut-help"
                    @click="chooseCommand('shortcuts')"
                  >
                    {{ t('palette.shortcutHelp') }}
                  </button>
                </div>
              </ListboxRoot>
            </TabsContent>
          </TabsRoot>
        </template>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
