<script setup lang="ts">
import {
  computed,
  defineAsyncComponent,
  h,
  nextTick,
  onMounted,
  onBeforeUnmount,
  ref,
  shallowRef,
  useTemplateRef,
  watch,
} from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { ApiError } from '../api'
import type { OfflineDocument } from '../offline/offlineDocument'
import type { CachedPage } from '../offline/types'
import { useAuthStore } from '../stores/auth'
import { useCurrentPageStore } from '../stores/currentPage'
import { useOfflineStore } from '../stores/offline'
import { useRecentStore } from '../stores/recent'
import { useSyncStore } from '../stores/sync'
import { useTreeStore } from '../stores/tree'
import SyncStatus from '../sync/SyncStatus.vue'
import BacklinksPanel from './BacklinksPanel.vue'
import ChildPages from './ChildPages.vue'
import FavoriteButton from './FavoriteButton.vue'
import PageTags from './PageTags.vue'
import TitleField from './TitleField.vue'
import type { PageDetail } from '../tree/types'

type PageEditorComponent = typeof import('../editor/PageEditor.vue').default

// The title and the pill show at once; the editor (TipTap and the rest, the larger part of the page's chunk) follows.
// The placeholder has the editor's minimum height, so nothing jumps when it arrives.
const PageEditor = defineAsyncComponent({
  loader: () => import('../editor/PageEditor.vue'),
  loadingComponent: { render: () => h('div', { class: 'min-h-48', 'aria-busy': 'true' }) },
  delay: 0,
})

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const tree = useTreeStore()
const sync = useSyncStore()
const auth = useAuthStore()
const offline = useOfflineStore()
const recent = useRecentStore()
const current = useCurrentPageStore()
const titleField = useTemplateRef<InstanceType<typeof TitleField>>('titleField')

const pageId = computed(() => String(route.params.pageId))
const detail = ref<PageDetail | null>(null)
const cached = shallowRef<CachedPage | null>(null)
const document = shallowRef<OfflineDocument | null>(null)
const browserOnline = ref(navigator.onLine)
const editorReady = ref(false)
const storageFailed = ref(false)
const offlineMissing = ref(false)
const retryFailed = ref(false)
const loadFailed = ref(false)
const missing = ref(false)
// As in the shell: offline for real, not a socket that is reconnecting, so the title and tags do not come and go with it.
const localOnly = computed(() => auth.offline || !browserOnline.value)
const canReachApi = computed(() => browserOnline.value && !auth.offline)
const retryCount = ref(0)
let loadGeneration = 0
let loadedId = ''
const updateNetwork = () => (browserOnline.value = navigator.onLine)

onMounted(() => {
  window.addEventListener('online', updateNetwork)
  window.addEventListener('offline', updateNetwork)
})

function discardIfUnused(session: OfflineDocument, id: string) {
  if (id === pageId.value || sync.ownsDocument(session)) return
  offline.release(session)
  session.doc.destroy()
}

async function openCached(id: string, userId: string | null, mine: number) {
  if (!userId) {
    if (mine === loadGeneration) offlineMissing.value = true
    return
  }
  try {
    const repository = await offline.repository()
    const saved = await repository.findPage(userId, id)
    if (mine !== loadGeneration) return
    if (!saved?.ready) {
      offlineMissing.value = true
      return
    }
    const session = await offline.prepare(saved, false)
    if (mine !== loadGeneration) {
      discardIfUnused(session, id)
      return
    }
    cached.value = saved
    document.value = session
    editorReady.value = true
    loadedId = id
  } catch (error) {
    if (mine !== loadGeneration) return
    console.error('The offline page could not be opened', error)
    storageFailed.value = true
  }
}

async function prepareOnline(found: PageDetail, userId: string, mine: number) {
  const id = found.page.id
  try {
    const repository = await offline.repository()
    const previous = await repository.findPage(userId, id)
    const roleKnown = tree.workspace?.id === found.workspaceId
    const metadata: CachedPage = {
      userId,
      workspaceId: found.workspaceId,
      pageId: id,
      title: found.page.title,
      inTrash: found.inTrash,
      ownTrash: found.page.deletedAt !== undefined,
      canEdit: roleKnown ? tree.canEdit : (previous?.canEdit ?? false),
      ready: previous?.workspaceId === found.workspaceId && previous.ready,
      lastOpened: Date.now(),
    }
    const session = await offline.prepare(metadata, true)
    if (mine !== loadGeneration) {
      discardIfUnused(session, id)
      return
    }
    session.refreshMetadata({ ...metadata, canEdit: session.metadata.canEdit })
    document.value = session
  } catch (error) {
    if (mine !== loadGeneration) return
    console.error('The page could not be saved for offline use', error)
    storageFailed.value = true
  }
  if (mine !== loadGeneration) return
  detail.value = found
  cached.value = null
  editorReady.value = true
  loadedId = id
}

// Read again when the page changes and when anything in the tree does, so a rename or a trash shows here at once.
watch(
  [pageId, () => tree.treeVersion, localOnly, canReachApi, retryCount],
  async ([id, , isLocal, hasApi]) => {
    const mine = ++loadGeneration
    if (loadedId !== id) {
      detail.value = null
      cached.value = null
      document.value = null
      editorReady.value = false
    }
    missing.value = false
    offlineMissing.value = false
    loadFailed.value = false
    if (!hasApi && isLocal && loadedId === id && editorReady.value) return
    const userId = auth.user?.id ?? auth.offlineUserId
    if (!hasApi) {
      await openCached(id, userId, mine)
      return
    }
    let found: PageDetail | null
    try {
      found = await tree.fetchPage(id)
    } catch (error) {
      if (mine !== loadGeneration) return
      if (error instanceof ApiError && error.code === 'network_error') {
        await openCached(id, userId, mine)
      } else if (error instanceof ApiError && (error.status === 403 || error.status === 404)) {
        missing.value = true
      } else {
        console.error('The page could not be loaded', error)
        loadFailed.value = true
      }
      return
    }
    if (mine !== loadGeneration) return
    missing.value = found === null
    if (!found || !userId) return
    storageFailed.value = false
    await prepareOnline(found, userId, mine)
    if (mine !== loadGeneration) return
    if (found && current.consumeTitleFocus(id)) {
      await nextTick()
      titleField.value?.focus()
    }
  },
  { immediate: true },
)

// Once when a page opens, not when its tree or tags are read again. A failure is ignored.
watch(
  () => (localOnly.value ? null : detail.value?.page.id),
  (id) => {
    if (id) void recent.visit(id)
  },
  { immediate: true },
)

// The editor opens the page's content over the tab's one socket; the view only reads what the socket says about it.
const syncState = computed(() => sync.pages[pageId.value]?.state)
// Remembered: showing the not-found message unmounts the editor, which leaves the page and clears its state, and the
// editor would come back to ask again.
const missingOnSocket = ref(false)
watch(pageId, () => (missingOnSocket.value = false))
watch(syncState, (state) => {
  if (state === 'missing') missingOnSocket.value = true
  else if (state === 'synced') missingOnSocket.value = false
})
const notFound = computed(() => missing.value || missingOnSocket.value)

// What is on the screen, for the tab's title and the announcement: the page as fetched, in the trash or not.
watch(
  [pageId, detail, cached, notFound],
  ([id, found, saved, isNotFound]) => {
    if (isNotFound) current.show(id, '', true)
    else if (found && found.page.id === id) current.show(id, found.page.title, false, found.inTrash)
    else if (saved && saved.pageId === id) current.show(id, saved.title, false, saved.inTrash)
  },
  { immediate: true },
)
onBeforeUnmount(() => {
  window.removeEventListener('online', updateNetwork)
  window.removeEventListener('offline', updateNetwork)
  current.clear()
})

const ownTrash = computed(() => detail.value?.page.deletedAt !== undefined)
const shownId = computed(() => detail.value?.page.id ?? cached.value?.pageId)
const shownTitle = computed(() => detail.value?.page.title ?? cached.value?.title ?? '')
const shownInTrash = computed(() => detail.value?.inTrash ?? cached.value?.inTrash ?? false)
const canEdit = computed(() => (auth.offline ? (cached.value?.canEdit ?? false) : tree.canEdit))
const localStatus = computed(() => (shownId.value ? sync.localStatus(shownId.value) : null))
const saveError = computed(() => storageFailed.value || !!localStatus.value?.error)
function retryStorage() {
  if (document.value) offline.retry(document.value.metadata)
  else retryCount.value++
}
async function retryUnavailable() {
  try {
    await auth.load()
    if (!auth.user) {
      const redirect = route.fullPath
      await router.replace({
        name: 'login',
        query: redirect && redirect !== '/' ? { redirect } : {},
      })
      return
    }
    if (!tree.loaded) await tree.init()
    retryCount.value++
  } catch (error) {
    if (!(error instanceof ApiError) || error.code !== 'network_error')
      console.error('The unavailable page could not be retried', error)
    retryFailed.value = true
  }
}
const editor = useTemplateRef<InstanceType<PageEditorComponent>>('editor')
</script>

<template>
  <section
    v-if="offlineMissing"
    class="mx-auto flex max-w-2xl flex-col gap-space-md p-margin md:p-margin-desktop"
  >
    <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('offline.unavailableTitle') }}</h1>
    <p>{{ t('offline.unavailable') }}</p>
    <p v-if="retryFailed" role="alert">{{ t('offline.retryFailed') }}</p>
    <button
      type="button"
      class="min-h-11 self-start rounded border border-border-strong px-space-lg"
      @click="retryUnavailable"
    >
      {{ t('offline.retry') }}
    </button>
  </section>

  <section
    v-else-if="loadFailed"
    role="alert"
    class="mx-auto flex max-w-2xl flex-col gap-space-md p-margin md:p-margin-desktop"
  >
    {{ t('offline.pageLoadFailed') }}
  </section>

  <section
    v-else-if="storageFailed && !editorReady"
    role="alert"
    class="mx-auto flex max-w-2xl flex-col gap-space-md p-margin md:p-margin-desktop"
  >
    <p>{{ t('offline.storageFailed') }}</p>
    <button
      type="button"
      class="min-h-11 self-start rounded border border-border-strong px-space-lg"
      @click="retryStorage"
    >
      {{ t('offline.retry') }}
    </button>
  </section>

  <section
    v-else-if="notFound"
    class="mx-auto flex max-w-2xl flex-col gap-space-md p-margin md:p-margin-desktop"
  >
    <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('page.notFound') }}</h1>
    <p
      v-if="sync.pages[pageId]?.unsent && localStatus && !localStatus.pending && !localStatus.error"
      role="alert"
    >
      {{ t('offline.accessRevokedPending') }}
    </p>
    <p v-else-if="sync.pages[pageId]?.unsent" role="alert">
      {{ t('offline.accessRevokedUnsafe') }}
    </p>
    <RouterLink :to="{ name: 'home' }" class="self-start text-primary underline">{{
      t('page.backToStart')
    }}</RouterLink>
  </section>

  <article
    v-else-if="shownId && editorReady"
    class="mx-auto flex max-w-3xl flex-col gap-space-lg p-margin md:p-margin-desktop"
  >
    <div
      v-if="shownInTrash"
      role="status"
      class="flex flex-wrap items-center gap-space-md rounded border border-border-strong bg-surface-elevated p-space-md text-body-sm"
    >
      <p class="flex-1">
        {{
          (detail ? ownTrash : cached?.ownTrash)
            ? t('page.inTrash')
            : t('page.inTrashThroughParent')
        }}
      </p>
      <button
        v-if="!localOnly && ownTrash && tree.canEdit && detail"
        type="button"
        class="min-h-11 rounded border border-border-strong px-space-lg text-label-md hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
        @click="tree.restore(detail.page.id)"
      >
        {{ t('trash.restore') }}
      </button>
    </div>

    <div class="flex items-center justify-end gap-space-sm">
      <FavoriteButton
        v-if="!localOnly && detail"
        :page-id="detail.page.id"
        :in-trash="detail.inTrash"
      />
      <SyncStatus :page-id="shownId" />
    </div>

    <div
      v-if="saveError"
      role="alert"
      class="rounded border border-error p-space-md text-body-sm text-error"
    >
      <p>{{ t('offline.storageFailed') }}</p>
      <button
        type="button"
        class="min-h-11 rounded border border-error px-space-md disabled:opacity-50"
        @click="retryStorage"
      >
        {{ t('offline.retry') }}
      </button>
    </div>

    <p
      v-if="syncState === 'blocked'"
      role="alert"
      class="rounded border border-error p-space-md text-body-sm text-error"
    >
      {{ t('offline.accessChanged') }}
    </p>

    <p
      v-if="syncState === 'tooLarge'"
      role="alert"
      class="rounded border border-error p-space-md text-body-sm text-error"
    >
      {{ t('sync.tooLarge') }}
    </p>

    <!-- The title is metadata of the page tree, not part of the document. The content of a page in the trash stays editable.
         Offline it stays on the screen, read-only, so a title still being typed is kept and saved when the network is back. -->
    <TitleField
      v-if="detail"
      ref="titleField"
      :key="`title-${detail.page.id}`"
      :page-id="detail.page.id"
      :title="detail.page.title"
      :read-only="localOnly || !tree.canEdit || detail.inTrash"
      @enter="editor?.focus()"
    />
    <h1 v-else class="text-headline-xl-mobile md:text-headline-xl">
      {{ shownTitle || t('tree.untitled') }}
    </h1>
    <PageTags
      v-if="detail"
      :key="`tags-${detail.page.id}`"
      :page-id="detail.page.id"
      :tags="detail.tags"
      :read-only="localOnly || !tree.canEdit || detail.inTrash"
    />
    <ChildPages v-if="detail" :page-id="detail.page.id" />
    <PageEditor
      :key="shownId"
      ref="editor"
      :page-id="shownId"
      :read-only="!canEdit"
      :document="document"
    />
    <BacklinksPanel v-if="detail" :key="`backlinks-${detail.page.id}`" :page-id="detail.page.id" />
  </article>
</template>
