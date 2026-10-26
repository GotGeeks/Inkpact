<script setup lang="ts">
import {
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterView, useRoute, useRouter } from 'vue-router'
import AppIcon from '../components/AppIcon.vue'
import { useMediaQuery } from '../composables/useMediaQuery'
import { useAuthStore } from '../stores/auth'
import { useNotificationsStore } from '../stores/notifications'
import { useSyncStore } from '../stores/sync'
import { useTreeStore } from '../stores/tree'
import SidebarContent from './SidebarContent.vue'
import NoticeToaster from './NoticeToaster.vue'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const tree = useTreeStore()
const notifications = useNotificationsStore()
const sync = useSyncStore()
const auth = useAuthStore()

// From the md breakpoint (48 rem) the sidebar stays beside the page; below it, a drawer opens from a button.
const wide = useMediaQuery('(min-width: 48rem)', true)
const drawerOpen = ref(false)
const failed = ref(false)

const selectedId = computed(() => (route.name === 'page' ? String(route.params.pageId) : null))

async function start() {
  failed.value = false
  try {
    await tree.init()
  } catch {
    failed.value = true
  }
}

onMounted(start)
onBeforeUnmount(() => {
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
          class="flex size-11 items-center justify-center rounded text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary-container"
          @click="drawerOpen = true"
        >
          <AppIcon name="menu" class="size-6" />
        </button>
        <p class="truncate text-headline-sm">{{ t('app.name') }}</p>
      </header>

      <main class="min-h-0 flex-1 overflow-y-auto" tabindex="-1">
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

    <NoticeToaster />
  </div>
</template>
