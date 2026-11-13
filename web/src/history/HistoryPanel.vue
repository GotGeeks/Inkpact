<script setup lang="ts">
import {
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { computed, nextTick, onMounted, ref, useTemplateRef } from 'vue'
import { useI18n } from 'vue-i18n'
import AppButton from '../components/AppButton.vue'
import AppIcon from '../components/AppIcon.vue'
import AppNotice from '../components/AppNotice.vue'
import { useMediaQuery } from '../composables/useMediaQuery'
import { useTreeStore } from '../stores/tree'
import type { RestoreAnswer, VersionSummary } from './types'
import { useVersionHistory } from './useVersionHistory'
import VersionPreview from './VersionPreview.vue'

/**
 * A page's history (run B.5b): its versions, newest first, and one of them to look at. Full screen on a phone, a sheet at
 * the side from the `md` breakpoint. On a phone the list and the version take turns; from `md` they are side by side.
 * Looking at a version changes nothing: it is shown by an editor of its own (VersionPreview), not by the page's.
 * Restoring one asks first, says what it replaces and what it leaves, and is one request at a time.
 */
const props = withDefaults(
  defineProps<{
    pageId: string
    pageTitle: string
    /** The person may edit the page. */
    canEdit?: boolean
    /** The page is in the trash, or below a page that is: restored from the trash first. */
    inTrash?: boolean
  }>(),
  { canEdit: false, inTrash: false },
)
const emit = defineEmits<{ close: []; restored: [answer: RestoreAnswer] }>()

const { t, locale } = useI18n()
const tree = useTreeStore()
// Made for one page: the page view closes the panel when another page opens.
const history = useVersionHistory(props.pageId)
const { list, details, restoring } = history
const wide = useMediaQuery('(min-width: 48rem)', true)

const selectedId = ref<string | null>(null)
const selected = computed(() =>
  selectedId.value ? list.versions.find((version) => version.id === selectedId.value) : undefined,
)
const selectedDetail = computed(() =>
  selectedId.value ? details.get(selectedId.value) : undefined,
)
const previewHeading = useTemplateRef<HTMLElement>('previewHeading')
const listElement = useTemplateRef<HTMLElement>('listElement')

onMounted(() => void history.load())

// ---- restoring -----------------------------------------------------------------------------------------------------------

/** Why a version cannot be restored here, for an editor; null when it can. A viewer is told nothing: the button is not there. */
const restoreBlocked = computed(() => {
  if (props.inTrash) return t('history.restore.inTrash')
  if (list.state === 'off') return t('history.restore.historyOff')
  return null
})
/** The confirmation is showing, for the version that is chosen. */
const confirming = ref(false)
const confirmHeading = useTemplateRef<HTMLElement>('confirmHeading')
const restoreButton = useTemplateRef<InstanceType<typeof AppButton>>('restoreButton')

async function askToRestore() {
  restoring.error = null
  confirming.value = true
  await nextTick()
  confirmHeading.value?.focus()
}

async function cancelRestore() {
  confirming.value = false
  await nextTick()
  ;(restoreButton.value?.$el as HTMLElement | undefined)?.focus()
}

async function confirmRestore() {
  const version = selected.value
  if (!version) return
  // The page's metadata version (its title and place) from the tree, as a rename or a move sends it. A page that is not in
  // the tree cannot be restored here (it is in the trash); 0 is refused as page_changed, and Refresh reads the tree.
  const answer = await history.restore(version.id, tree.byId.get(props.pageId)?.version ?? 0)
  if (answer) {
    confirming.value = false
    emit('restored', answer)
  }
}

/**
 * The page changed since the list was read: the list and the tree are read again (the content mark and the metadata
 * version a restore sends back), and the version stays on the screen.
 */
async function refreshAfterChange() {
  restoring.error = null
  confirming.value = false
  await Promise.all([history.load(), tree.load().catch(() => undefined)])
}

/** Why a restore was refused. The codes a version's preview has too are said as the preview says them. */
function restoreError(code: string): string {
  switch (code) {
    case 'page_changed':
      return t('history.restore.pageChanged')
    case 'page_in_trash':
      return t('history.restore.inTrash')
    case 'history_off':
      return t('history.restore.historyOff')
    case 'history_full':
      return t('history.restore.full')
    case 'attachment_missing':
      return t('history.restore.attachmentMissing')
    case 'content_too_large':
      return t('history.restore.tooLarge')
    case 'page_unreadable':
      return t('history.restore.pageUnreadable')
    case 'forbidden':
      return t('history.restore.forbidden')
    // The server may have restored it without the answer arriving: trying again asks for that answer (same operation id).
    case 'network_error':
    case 'server_error':
      return t('history.restore.noAnswer')
    case 'version_not_found':
    case 'version_unreadable':
    case 'history_unavailable':
      return previewError(code)
    default:
      return t('history.restore.failed')
  }
}

/**
 * Until when each version is kept whatever the retention says (only the page as it was before a restore), by id. The
 * server leaves `protectedUntil` out once it has passed, so its clock decides, not the browser's.
 */
const keptUntil = computed(() => {
  const kept = new Map<string, string>()
  for (const version of list.versions)
    if (version.protectedUntil)
      kept.set(version.id, t('history.keptUntil', { time: when(version.protectedUntil) }))
  return kept
})

const timeFormat = computed(
  () => new Intl.DateTimeFormat(locale.value, { dateStyle: 'medium', timeStyle: 'short' }),
)
const when = (iso: string) => timeFormat.value.format(new Date(iso))

// The keys written out, so the lint rule that finds unused translations can see them.
function kindLabel(version: VersionSummary): string {
  switch (version.kind) {
    case 'automatic':
      return t('history.kind.automatic')
    case 'rename':
      return t('history.kind.rename')
    case 'restore':
      return t('history.kind.restore')
    case 'before_restore':
      return t('history.kind.beforeRestore')
  }
}

/** What kind of version, and who made it when that is one person. */
function byLine(version: VersionSummary): string {
  const kind = kindLabel(version)
  return version.createdBy
    ? t('history.byAuthor', { kind, name: version.createdBy.displayName })
    : kind
}

const notice = computed(() => {
  if (!list.loaded) return null
  switch (list.state) {
    case 'pending':
      return { tone: 'info' as const, text: t('history.state.pending') }
    case 'failed':
      return { tone: 'error' as const, text: t('history.state.failed') }
    case 'full':
      return { tone: 'info' as const, text: t('history.state.full') }
    case 'off':
      return { tone: 'info' as const, text: t('history.state.off') }
    default:
      return null
  }
})

function previewError(code: string): string {
  switch (code) {
    case 'history_unavailable':
      return t('history.preview.unavailable')
    case 'version_unreadable':
      return t('history.versionUnreadable')
    case 'version_not_found':
      return t('history.preview.notFound')
    default:
      return t('history.preview.failed')
  }
}
/** Trying again helps when the sidecar or the network was the reason, not when the version is gone or cannot be read. */
const canRetry = (code: string) => code !== 'version_unreadable' && code !== 'version_not_found'

async function choose(version: VersionSummary) {
  // While a restore is on its way, its answer belongs to the version on the screen: the rows wait (they are disabled).
  if (restoring.busy) return
  confirming.value = false
  restoring.error = null
  selectedId.value = version.id
  void history.open(version.id)
  // On a phone the list goes away for the version: the focus goes with it, to the version's heading.
  if (!wide.value) {
    await nextTick()
    previewHeading.value?.focus()
  }
}

async function backToList() {
  const id = selectedId.value
  selectedId.value = null
  await nextTick()
  if (id)
    listElement.value?.querySelector<HTMLElement>(`[data-version-id="${CSS.escape(id)}"]`)?.focus()
}

const row =
  'flex min-h-11 w-full flex-col items-start gap-space-xs rounded px-space-md py-space-sm text-start hover:bg-surface-container-high focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary'
</script>

<template>
  <DialogRoot :open="true" @update:open="(open: boolean) => !open && emit('close')">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-40 bg-surface-canvas/70" />
      <DialogContent
        data-testid="history-panel"
        class="fixed inset-0 z-50 flex flex-col bg-surface-panel text-on-surface md:start-auto md:w-full md:max-w-4xl md:border-s md:border-border-subtle md:shadow-lg"
      >
        <header class="flex items-center gap-space-md border-b border-border-subtle p-space-md">
          <DialogTitle class="min-w-0 flex-1 truncate text-headline-sm">
            {{ t('history.title', { title: pageTitle || t('tree.untitled') }) }}
          </DialogTitle>
          <DialogClose
            :aria-label="t('history.close')"
            class="flex size-11 shrink-0 items-center justify-center rounded hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary md:size-9"
          >
            <AppIcon name="close" class="size-5" />
          </DialogClose>
        </header>
        <DialogDescription class="sr-only">{{ t('history.description') }}</DialogDescription>

        <AppNotice v-if="notice" :tone="notice.tone" data-testid="history-state">
          {{ notice.text }}
        </AppNotice>

        <div class="flex min-h-0 flex-1">
          <!-- The versions. -->
          <section
            ref="listElement"
            :aria-label="t('history.listLabel')"
            class="min-h-0 w-full flex-col gap-space-sm overflow-y-auto p-space-sm md:flex md:w-80 md:shrink-0 md:border-e md:border-border-subtle"
            :class="selectedId ? 'hidden' : 'flex'"
          >
            <p v-if="list.loading && !list.loaded" role="status" class="p-space-md text-body-sm">
              {{ t('history.loading') }}
            </p>
            <div
              v-else-if="list.error"
              role="alert"
              class="flex flex-col items-start gap-space-sm p-space-md text-body-sm"
            >
              <p>{{ t('history.loadFailed') }}</p>
              <AppButton @click="history.load()">{{ t('history.retry') }}</AppButton>
            </div>
            <p
              v-else-if="list.loaded && list.versions.length === 0"
              data-testid="history-empty"
              class="p-space-md text-body-sm text-on-surface-variant"
            >
              {{ list.state === 'off' ? t('history.emptyOff') : t('history.empty') }}
            </p>
            <ul v-else-if="list.loaded" class="flex flex-col" data-testid="version-list">
              <li v-for="version in list.versions" :key="version.id">
                <button
                  type="button"
                  :class="[row, version.id === selectedId ? 'bg-surface-container-high' : '']"
                  :aria-current="version.id === selectedId ? 'true' : undefined"
                  :data-version-id="version.id"
                  :disabled="restoring.busy"
                  data-testid="version-row"
                  @click="choose(version)"
                >
                  <span class="text-label-md">{{ when(version.createdAt) }}</span>
                  <span class="text-body-sm text-on-surface-variant">
                    {{ byLine(version) }}
                  </span>
                  <span class="w-full truncate text-body-sm">{{
                    version.title || t('tree.untitled')
                  }}</span>
                  <span
                    v-if="keptUntil.has(version.id)"
                    class="text-label-xs text-on-surface-variant"
                    >{{ keptUntil.get(version.id) }}</span
                  >
                </button>
              </li>
            </ul>
            <template v-if="list.loaded && list.next">
              <p v-if="list.moreError" role="alert" class="px-space-md text-body-sm text-error">
                {{ t('history.moreFailed') }}
              </p>
              <AppButton
                class="self-start"
                :disabled="list.loadingMore"
                data-testid="history-more"
                @click="history.loadMore()"
              >
                {{ list.loadingMore ? t('history.loadingMore') : t('history.more') }}
              </AppButton>
            </template>
          </section>

          <!-- The version that was chosen. -->
          <section
            class="min-w-0 flex-1 flex-col gap-space-md overflow-y-auto p-space-lg md:flex"
            :class="selectedId ? 'flex' : 'hidden'"
            data-testid="version-preview"
          >
            <p v-if="!selected" class="text-body-sm text-on-surface-variant">
              {{ t('history.choose') }}
            </p>
            <template v-else>
              <AppButton
                variant="text"
                class="self-start md:hidden"
                :disabled="restoring.busy"
                @click="backToList"
              >
                {{ t('history.back') }}
              </AppButton>
              <h3
                ref="previewHeading"
                tabindex="-1"
                class="text-label-md focus-visible:outline-2 focus-visible:outline-primary"
              >
                {{
                  t('history.heading', {
                    time: when(selected.createdAt),
                    kind: kindLabel(selected),
                  })
                }}
              </h3>
              <p
                v-if="selectedDetail?.status === 'loading'"
                role="status"
                class="text-body-sm text-on-surface-variant"
              >
                {{ t('history.preview.loading') }}
              </p>
              <div
                v-else-if="selectedDetail?.status === 'error'"
                role="alert"
                class="flex flex-col items-start gap-space-sm text-body-sm"
              >
                <p>{{ previewError(selectedDetail.code) }}</p>
                <AppButton v-if="canRetry(selectedDetail.code)" @click="history.open(selected.id)">
                  {{ t('history.retry') }}
                </AppButton>
              </div>
              <template v-else-if="selectedDetail?.status === 'ready'">
                <p class="text-headline-lg" data-testid="version-title">
                  {{ selectedDetail.detail.title || t('tree.untitled') }}
                </p>
                <div
                  v-if="canEdit"
                  class="flex flex-col gap-space-sm border-y border-border-subtle py-space-md"
                  data-testid="restore-area"
                >
                  <p v-if="restoreBlocked" class="text-body-sm text-on-surface-variant">
                    {{ restoreBlocked }}
                  </p>
                  <AppButton
                    v-else-if="!confirming"
                    ref="restoreButton"
                    variant="primary"
                    class="self-start"
                    data-testid="restore-version"
                    @click="askToRestore"
                  >
                    {{ t('history.restore.action') }}
                  </AppButton>
                  <div
                    v-else
                    role="group"
                    aria-labelledby="restore-confirm-heading"
                    class="flex flex-col gap-space-sm"
                    data-testid="restore-confirm"
                  >
                    <h4
                      id="restore-confirm-heading"
                      ref="confirmHeading"
                      tabindex="-1"
                      class="text-label-md focus-visible:outline-2 focus-visible:outline-primary"
                    >
                      {{ t('history.restore.confirmTitle') }}
                    </h4>
                    <p class="text-body-sm">{{ t('history.restore.replaces') }}</p>
                    <p class="text-body-sm text-on-surface-variant">
                      {{ t('history.restore.keeps') }}
                    </p>
                    <div class="flex flex-wrap gap-space-sm">
                      <AppButton
                        variant="primary"
                        :disabled="restoring.busy"
                        data-testid="restore-confirm-button"
                        @click="confirmRestore"
                      >
                        {{
                          restoring.busy ? t('history.restore.busy') : t('history.restore.confirm')
                        }}
                      </AppButton>
                      <AppButton :disabled="restoring.busy" @click="cancelRestore">
                        {{ t('history.restore.cancel') }}
                      </AppButton>
                    </div>
                  </div>
                  <div
                    v-if="restoring.error"
                    role="alert"
                    class="flex flex-col items-start gap-space-sm text-body-sm"
                    data-testid="restore-error"
                  >
                    <p>{{ restoreError(restoring.error) }}</p>
                    <AppButton
                      v-if="restoring.error === 'page_changed'"
                      @click="refreshAfterChange"
                    >
                      {{ t('history.restore.refresh') }}
                    </AppButton>
                  </div>
                </div>
                <!-- Made again in another language: its words are set when it is made. -->
                <VersionPreview
                  :key="`${selected.id}-${locale}`"
                  :content="selectedDetail.detail.content"
                  :label="t('history.preview.label')"
                />
              </template>
            </template>
          </section>
        </div>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
