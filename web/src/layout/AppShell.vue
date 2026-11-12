<script setup lang="ts">
import {
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterView, useRoute, useRouter } from 'vue-router'
import { ApiError } from '../api'
import AppButton from '../components/AppButton.vue'
import AppIcon from '../components/AppIcon.vue'
import AppNotice from '../components/AppNotice.vue'
import SearchDialog from '../search/SearchDialog.vue'
import { editorTakesSearchShortcut } from '../editor/linkShortcutRegistry'
import { availableCommands, type PaletteCommandId } from '../palette/commands'
import { isSearchShortcut } from '../search/shortcut'
import { isCommandPaletteShortcut, isNewPageShortcut } from '../search/commandShortcuts'
import { useMediaQuery } from '../composables/useMediaQuery'
import { useOnline } from '../composables/useOnline'
import { loginRoute } from '../router'
import { useAuthStore } from '../stores/auth'
import { useCurrentPageStore } from '../stores/currentPage'
import { useFavoritesStore } from '../stores/favorites'
import { useNotificationsStore } from '../stores/notifications'
import { useOfflineStore } from '../stores/offline'
import { useRecentStore } from '../stores/recent'
import { useSearchStore } from '../stores/search'
import { useSyncStore } from '../stores/sync'
import { useTagsStore } from '../stores/tags'
import { useTreeStore } from '../stores/tree'
import { isOfflineIdentityEvent, readOfflineIdentity } from '../offline/identity'
import SidebarContent from './SidebarContent.vue'
import NoticeToaster from './NoticeToaster.vue'
import { useKeepLostEdits } from '../sync/useKeepLostEdits'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const tree = useTreeStore()
const notifications = useNotificationsStore()
const search = useSearchStore()
const sync = useSyncStore()
const auth = useAuthStore()
const offline = useOfflineStore()
const tags = useTagsStore()
const favorites = useFavoritesStore()
const recent = useRecentStore()
useKeepLostEdits()

// From the md breakpoint (48 rem) the sidebar stays beside the page; below it, a drawer opens from a button.
const wide = useMediaQuery('(min-width: 48rem)', true)
const drawerOpen = ref(false)
const failed = ref(false)
const browserOnline = useOnline((online) => {
  if (online && auth.offline) void revalidateSession()
})
// Offline for real: the app started without the server, or the browser has no network. Not a socket that is reconnecting
// (a server restart, a phone waking up): the tree, search and the header stay while the sync client tries again.
const localOnly = computed(() => auth.offline || !browserOnline.value)
const routeReady = ref(auth.offline || tree.loaded)
let starting: Promise<void> | null = null

const selectedId = computed(() => (route.name === 'page' ? String(route.params.pageId) : null))

const main = useTemplateRef<HTMLElement>('main')
// Said once when a page opens, with the focus left where it was: a keyboard user walking the tree keeps their place. The
// page is the one the view fetched, so a page in the trash is said too. The text is cleared first, so that opening a
// second page with the same title ("Untitled") changes the region and is said again.
const current = useCurrentPageStore()
const announcement = ref('')
watch(
  () => (current.id && !current.missing ? current.id : null),
  async (id) => {
    announcement.value = ''
    if (!id) return
    await nextTick()
    announcement.value = t('a11y.opened', { title: current.title || t('tree.untitled') })
  },
)

/** The skip link: past the sidebar to the page, without an address change. */
function skipToPage() {
  main.value?.focus()
}

async function revalidateSession() {
  try {
    await auth.load()
    if (!auth.user) sync.identityChanged()
  } catch (error) {
    if (!(error instanceof ApiError) || error.code !== 'network_error')
      console.error('The offline session could not be revalidated', error)
  }
}

function onStorage(event: StorageEvent) {
  if (!isOfflineIdentityEvent(event)) return
  const expected = auth.user?.id ?? auth.offlineUserId
  if (expected && readOfflineIdentity() !== expected) sync.identityChanged()
}

function start(): Promise<void> {
  if (starting) return starting
  starting = startOnce().finally(() => (starting = null))
  return starting
}

async function startOnce() {
  failed.value = false
  try {
    await tree.init()
    routeReady.value = tree.loaded
    void favorites.load()
  } catch (error) {
    if (error instanceof ApiError && error.code === 'network_error' && route.name === 'page') {
      auth.enterOffline()
      routeReady.value = true
    } else {
      console.error('The page tree could not be loaded', error)
      failed.value = true
    }
  }
}

onMounted(() => {
  if (!auth.offline) void start()
  const userId = auth.user?.id ?? auth.offlineUserId
  if (userId) void sync.loadRecoveries(userId)
  favorites.watchFocus()
  window.addEventListener('storage', onStorage)
  // Capturing, so that the editor does not take the key first; the browser would otherwise use it for its own search.
  window.addEventListener('keydown', onShortcut, true)
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onShortcut, true)
  window.removeEventListener('storage', onStorage)
  search.reset()
  tree.reset()
  tags.reset()
  favorites.reset()
  recent.reset()
  sync.destroy()
})

watch(
  () => auth.offline,
  (isOffline) => {
    if (isOffline) routeReady.value = true
    else if (!tree.loaded) void start()
  },
)

// The server ended the session (or connecting kept failing and the sign-in state says so): back to the login page, and
// to this page again afterwards.
watch(
  () => sync.sessionEnded,
  async (ended) => {
    if (!ended) return
    if (sync.unsent || sync.unsafeToLeave || sync.lostEdits.length > 0) {
      sync.identityChanged()
      return
    }
    if (auth.user) {
      // Still signed in after all: start over with a new socket.
      window.location.reload()
      return
    }
    sync.destroy()
    await router.replace(loginRoute(route.fullPath))
  },
)

// A page is only found in the tree when the branches above it are open.
watch([selectedId, () => tree.loaded], ([id, loaded]) => {
  if (id && loaded) tree.reveal(id)
})

// The Undo notice for a trashed page cannot be reached behind the open drawer, so the drawer makes way for it.
watch(
  () => notifications.notices.length,
  () => {
    if (notifications.notices.at(-1)?.type === 'trashed') drawerOpen.value = false
  },
)

watch(wide, (isWide) => {
  if (isWide) drawerOpen.value = false
})

/** Ctrl+K, or Cmd+K on a Mac, from anywhere in the app, also inside the editor. */
async function onShortcut(event: KeyboardEvent) {
  if (localOnly.value) return
  if (isSearchShortcut(event)) {
    // In the editor, with text selected or the caret in a link, it makes a link (run B.1b): the editor takes the key itself.
    if (editorTakesSearchShortcut()) return
    if (drawerOpen.value) {
      event.preventDefault()
      event.stopPropagation()
      drawerOpen.value = false
      await nextTick()
    } else if (hasPaletteBlocker()) {
      return
    }
    event.preventDefault()
    event.stopPropagation()
    search.show('pages')
    return
  }

  const inText = isEditableTarget(event.target)
  // From the editor and from fields too, the palette's own field included: there it switches to Commands.
  if (isCommandPaletteShortcut(event, inText)) {
    if (drawerOpen.value) {
      event.preventDefault()
      event.stopPropagation()
      drawerOpen.value = false
      await nextTick()
    } else if (hasPaletteBlocker()) {
      return
    }
    event.preventDefault()
    event.stopPropagation()
    search.show('commands')
    return
  }

  if (isNewPageShortcut(event, inText)) {
    if (inText || hasOpenDialogOrMenu() || !tree.workspace || !tree.canEdit) {
      return
    }
    event.preventDefault()
    event.stopPropagation()
    void createTopLevelPage()
  }
}

function hasPaletteBlocker(): boolean {
  return (
    document.querySelector(
      '[role="dialog"]:not([data-testid="search-dialog"]), [role="alertdialog"], [role="menu"]',
    ) !== null
  )
}

function isEditableTarget(target: EventTarget | null): boolean {
  return (
    target instanceof HTMLElement &&
    (target.isContentEditable ||
      target.closest(
        'input, textarea, select, [contenteditable]:not([contenteditable="false"])',
      ) !== null)
  )
}

function hasOpenDialogOrMenu(): boolean {
  return document.querySelector('[role="dialog"], [role="alertdialog"], [role="menu"]') !== null
}

let pageCreation: Promise<void> | null = null

function createTopLevelPage(): Promise<void> {
  if (pageCreation) return pageCreation
  const request = createTopLevelPageOnce()
  pageCreation = request
  void request.then(
    () => {
      if (pageCreation === request) pageCreation = null
    },
    () => {
      if (pageCreation === request) pageCreation = null
    },
  )
  return request
}

async function createTopLevelPageOnce(): Promise<void> {
  if (!tree.workspace || !tree.canEdit) return
  const routeBefore = route.fullPath
  search.hide()
  await nextTick()
  // The dialog is opened programmatically, so let its focus restoration finish before tracking the request.
  await nextTick()
  const focusBefore = document.activeElement

  const result = await tree.create('', { parentId: null })
  if (!result) return

  const pageId = result.page.id
  if (route.fullPath !== routeBefore || document.activeElement !== focusBefore) {
    notifications.post({ type: 'pageCreated', pageId, title: result.page.title })
    return
  }

  current.requestTitleFocus(pageId)
  await router.push({ name: 'page', params: { pageId } })
  if (route.name !== 'page' || route.params.pageId !== pageId) {
    current.cancelTitleFocus(pageId)
    notifications.post({ type: 'pageCreated', pageId, title: result.page.title })
  }
}

function commandIsAvailable(id: PaletteCommandId): boolean {
  const currentId = current.id
  return availableCommands({
    canEdit: tree.canEdit,
    hasWorkspace: tree.workspace !== null,
    hasLiveCurrentPage:
      currentId !== null && !current.missing && !current.inTrash && tree.byId.has(currentId),
    favoritesReady: favorites.loaded && favorites.loadError === null,
    currentPageIsFavorite: currentId !== null && favorites.has(currentId),
  }).some((command) => command.id === id)
}

async function dispatchCommand(id: PaletteCommandId) {
  if (!commandIsAvailable(id)) return
  if (id === 'shortcuts') {
    search.showHelp()
    return
  }
  search.hide()
  drawerOpen.value = false
  await nextTick()
  switch (id) {
    case 'newPage':
      await createTopLevelPage()
      break
    case 'toggleFavorite':
      if (current.id && !current.inTrash && !current.missing && tree.byId.has(current.id))
        await favorites.toggle(current.id)
      break
    case 'home':
      await router.push({ name: 'home' })
      break
    case 'tags':
      await router.push({ name: 'tags' })
      break
    case 'trash':
      await router.push({ name: 'trash' })
      break
    case 'import':
      await router.push({ name: 'import' })
      break
  }
}

function show(id: string) {
  void router.push({ name: 'page', params: { pageId: id } })
}

function open(id: string) {
  drawerOpen.value = false
  show(id)
}

function reloadAfterAccountChange() {
  if (!sync.unsafeToLeave) window.location.reload()
}

function retryOfflineStorage() {
  void offline.retryAll()
  sync.retryRecoveries()
  const userId = auth.user?.id ?? auth.offlineUserId
  if (userId) void sync.loadRecoveries(userId)
}
</script>

<template>
  <main
    v-if="sync.accountChanged"
    class="mx-auto flex max-w-2xl flex-col items-start gap-space-md p-margin text-on-surface md:p-margin-desktop"
  >
    <h1 class="text-headline-xl-mobile md:text-headline-xl">
      {{ t('offline.accountChangedTitle') }}
    </h1>
    <p role="alert">{{ t('offline.accountChanged') }}</p>
    <p v-if="sync.unsafeToLeave">{{ t('offline.accountUnsafe') }}</p>
    <AppButton v-if="sync.unsafeToLeave" @click="retryOfflineStorage">
      {{ t('offline.retry') }}
    </AppButton>
    <AppButton :disabled="sync.unsafeToLeave" @click="reloadAfterAccountChange">
      {{ t('offline.continue') }}
    </AppButton>
  </main>
  <div v-else class="flex h-dvh bg-surface-canvas text-on-surface">
    <a
      href="#main"
      class="sr-only focus:not-sr-only focus:fixed focus:start-space-md focus:top-space-md focus:z-[70] focus:rounded focus:bg-primary-container focus:px-space-lg focus:py-space-sm focus:text-label-md focus:text-on-primary-container focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
      data-testid="skip-link"
      @click.prevent="skipToPage"
      >{{ t('a11y.skip') }}</a
    >
    <p class="sr-only" aria-live="polite" aria-atomic="true" data-testid="announcer">
      {{ announcement }}
    </p>
    <aside
      v-if="wide && !localOnly"
      class="w-80 shrink-0 border-e border-border-subtle"
      :aria-label="t('shell.sidebar')"
    >
      <SidebarContent :selected-id="selectedId" @open="open" @created="show" />
    </aside>

    <div class="flex min-w-0 flex-1 flex-col">
      <header
        v-if="!wide && !localOnly"
        class="flex min-h-14 items-center gap-space-sm border-b border-border-subtle bg-surface-panel px-space-sm"
      >
        <button
          type="button"
          :aria-label="t('shell.openMenu')"
          class="flex size-11 items-center justify-center rounded text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary"
          @click="drawerOpen = true"
        >
          <AppIcon name="menu" class="size-6" />
        </button>
        <p class="min-w-0 flex-1 truncate text-headline-sm">{{ t('app.name') }}</p>
        <button
          type="button"
          :aria-label="t('search.open')"
          class="flex size-11 items-center justify-center rounded text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary"
          data-testid="search-open"
          @click="search.show()"
        >
          <AppIcon name="search" class="size-6" />
        </button>
      </header>

      <main id="main" ref="main" class="min-h-0 flex-1 overflow-y-auto outline-none" tabindex="-1">
        <AppNotice v-if="localOnly">{{ t('offline.limited') }}</AppNotice>
        <AppNotice v-if="offline.evicted > 0">{{ t('offline.evicted') }}</AppNotice>
        <AppNotice v-if="offline.hasFailure" tone="error">
          {{ t('offline.storageAttention') }}
          <template #action>
            <AppButton variant="contained" @click="retryOfflineStorage">
              {{ t('offline.retry') }}
            </AppButton>
          </template>
        </AppNotice>
        <AppNotice v-if="failed" tone="error" data-testid="shell-load-error">
          {{ t('shell.loadFailed') }}
          <template #action>
            <AppButton variant="contained" @click="start">
              {{ t('shell.retry') }}
            </AppButton>
          </template>
        </AppNotice>
        <RouterView v-else-if="routeReady" />
      </main>
    </div>

    <DialogRoot v-if="!wide && !localOnly" v-model:open="drawerOpen">
      <DialogPortal>
        <DialogOverlay class="fixed inset-0 z-40 bg-surface-canvas/70" />
        <DialogContent
          class="fixed inset-y-0 start-0 z-50 w-[min(20rem,85vw)] border-e border-border-subtle bg-surface-panel text-on-surface shadow-lg outline-none"
        >
          <DialogTitle class="sr-only">{{ t('shell.sidebar') }}</DialogTitle>
          <DialogDescription class="sr-only">{{ t('shell.sidebarDescription') }}</DialogDescription>
          <SidebarContent
            :selected-id="selectedId"
            @open="open"
            @created="show"
            @navigate="drawerOpen = false"
          />
        </DialogContent>
      </DialogPortal>
    </DialogRoot>

    <SearchDialog v-if="!localOnly" @command="dispatchCommand" />

    <NoticeToaster />
  </div>
</template>
