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
import AppIcon from '../components/AppIcon.vue'
import SearchDialog from '../search/SearchDialog.vue'
import { editorTakesSearchShortcut } from '../editor/linkShortcutRegistry'
import { isSearchShortcut } from '../search/shortcut'
import { useMediaQuery } from '../composables/useMediaQuery'
import { useAuthStore } from '../stores/auth'
import { useCurrentPageStore } from '../stores/currentPage'
import { useNotificationsStore } from '../stores/notifications'
import { useSearchStore } from '../stores/search'
import { useSyncStore } from '../stores/sync'
import { useTreeStore } from '../stores/tree'
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
useKeepLostEdits()

// From the md breakpoint (48 rem) the sidebar stays beside the page; below it, a drawer opens from a button.
const wide = useMediaQuery('(min-width: 48rem)', true)
const drawerOpen = ref(false)
const failed = ref(false)

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

async function start() {
  failed.value = false
  try {
    await tree.init()
  } catch {
    failed.value = true
  }
}

onMounted(() => {
  void start()
  // Capturing, so that the editor does not take the key first; the browser would otherwise use it for its own search.
  window.addEventListener('keydown', onShortcut, true)
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onShortcut, true)
  search.reset()
  tree.reset()
  sync.destroy()
})

// The server ended the session (or connecting kept failing and the sign-in state says so): back to the login page, and
// to this page again afterwards.
watch(
  () => sync.sessionEnded,
  async (ended) => {
    if (!ended) return
    if (auth.user) {
      // Still signed in after all: start over with a new socket.
      window.location.reload()
      return
    }
    sync.destroy()
    const redirect = route.fullPath
    await router.replace({
      name: 'login',
      query: redirect && redirect !== '/' ? { redirect } : {},
    })
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
  if (!isSearchShortcut(event)) return
  // In the editor, with text selected or the caret in a link, it makes a link (run B.1b): the editor takes the key itself.
  if (editorTakesSearchShortcut()) return
  event.preventDefault()
  if (drawerOpen.value) {
    // Two dialogs do not share the focus: the drawer makes way, as for the Search row in it.
    drawerOpen.value = false
    await nextTick()
  } else if (
    document.querySelector(
      '[role="dialog"]:not([data-testid="search-dialog"]), [role="alertdialog"]',
    )
  ) {
    // Another dialog (moving a page, say) is being answered: it is finished or closed first.
    return
  }
  search.show()
}

function show(id: string) {
  void router.push({ name: 'page', params: { pageId: id } })
}

function open(id: string) {
  drawerOpen.value = false
  show(id)
}
</script>

<template>
  <div class="flex h-dvh bg-surface-canvas text-on-surface">
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
      v-if="wide"
      class="w-80 shrink-0 border-e border-border-subtle"
      :aria-label="t('shell.sidebar')"
    >
      <SidebarContent :selected-id="selectedId" @open="open" @created="show" />
    </aside>

    <div class="flex min-w-0 flex-1 flex-col">
      <header
        v-if="!wide"
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
        <div
          v-if="failed"
          role="alert"
          class="m-margin flex flex-col items-start gap-space-md rounded border border-error bg-error-container p-space-lg text-on-error-container"
        >
          <p>{{ t('shell.loadFailed') }}</p>
          <button
            type="button"
            class="min-h-11 rounded border border-on-error-container px-space-lg text-label-md md:min-h-9"
            @click="start"
          >
            {{ t('shell.retry') }}
          </button>
        </div>
        <RouterView v-else-if="tree.loaded" />
      </main>
    </div>

    <DialogRoot v-if="!wide" v-model:open="drawerOpen">
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

    <SearchDialog />

    <NoticeToaster />
  </div>
</template>
