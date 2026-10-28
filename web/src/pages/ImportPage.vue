<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'
import { ApiError } from '../api'
import AppIcon from '../components/AppIcon.vue'
import ProgressBar from '../components/ProgressBar.vue'
import { useErrorMessage } from '../errors'
import { formatFileSize } from '../fileSize'
import {
  ImportInProgressError,
  hasEnded,
  importMaxFileSize,
  startJoplinImport,
  type ReportStatus,
} from '../imports/importApi'
import { useImportMessages } from '../imports/useImportMessages'
import { useImportRun } from '../imports/useImportRun'
import { useTreeStore } from '../stores/tree'

const { t, n, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const tree = useTreeStore()
const errorMessage = useErrorMessage()
const messages = useImportMessages()

// The import is in the address, so a reload (or a link) shows it again: there is no list of imports to find it in.
const importId = computed(() =>
  typeof route.params.importId === 'string' && route.params.importId ? route.params.importId : null,
)

// The sidebar does not hear about pages the server made, so it reads the tree again when the import has ended.
const { run, error, connectionLost } = useImportRun(importId, {
  onEnded: () => void tree.load().catch(() => undefined),
})

const input = ref<HTMLInputElement | null>(null)
const upload = ref<{ name: string; sent: number; total: number } | null>(null)
// Why the upload did not start, and when the server said so, how long to wait or which import is in the way.
const uploadError = ref<{ code: string; retryAfterSeconds?: number; runningId?: string } | null>(
  null,
)
const maxFileSize = ref<number | null>(null)
let controller: AbortController | null = null

// Leaving the page ends the upload, which can be most of a large file: ask first, in the app and when the tab closes.
function keepUpload(event: BeforeUnloadEvent) {
  if (!upload.value) return
  event.preventDefault()
  event.returnValue = ''
}
onBeforeRouteLeave(() => !upload.value || window.confirm(t('import.upload.leaveConfirm')))

onMounted(async () => {
  window.addEventListener('beforeunload', keepUpload)
  maxFileSize.value = await importMaxFileSize()
})
onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', keepUpload)
  controller?.abort()
})

type Phase = 'start' | 'uploading' | 'unavailable' | 'loading' | 'running' | 'ended'
const phase = computed<Phase>(() => {
  if (upload.value) return 'uploading'
  if (!importId.value) return 'start'
  if (error.value) return 'unavailable'
  if (!run.value) return 'loading'
  return hasEnded(run.value) ? 'ended' : 'running'
})

const entries = computed(() => run.value?.report ?? [])
const pageSize = 100
const shown = ref(pageSize)
const visibleEntries = computed(() => entries.value.slice(0, shown.value))
const nextBatch = computed(() => Math.min(pageSize, entries.value.length - shown.value))

const counts = computed(() => {
  const c = run.value?.counts
  return [
    { key: 'notebooks', label: t('import.counts.notebooks'), value: c?.notebooks ?? 0 },
    { key: 'notes', label: t('import.counts.notes'), value: c?.notes ?? 0 },
    { key: 'todos', label: t('import.counts.todos'), value: c?.todos ?? 0 },
    { key: 'tags', label: t('import.counts.tags'), value: c?.tags ?? 0 },
    { key: 'images', label: t('import.counts.images'), value: c?.images ?? 0 },
    { key: 'files', label: t('import.counts.files'), value: c?.files ?? 0 },
  ]
})

// What was left alone, one line each, only for what applies.
const leftAlone = computed(() => {
  const c = run.value?.counts
  if (!c) return []
  return [
    {
      key: 'alreadyImported',
      text: t('import.left.alreadyImported', { count: n(c.alreadyImported) }, c.alreadyImported),
      value: c.alreadyImported,
    },
    {
      key: 'alreadyInTrash',
      text: t('import.left.alreadyInTrash', { count: n(c.alreadyInTrash) }, c.alreadyInTrash),
      value: c.alreadyInTrash,
    },
    {
      key: 'trashed',
      text: t('import.left.trashed', { count: n(c.trashed) }, c.trashed),
      value: c.trashed,
    },
    {
      key: 'skipped',
      text: t('import.left.skipped', { count: n(c.skipped) }, c.skipped),
      value: c.skipped,
    },
    {
      key: 'failed',
      text: t('import.left.failed', { count: n(c.failed) }, c.failed),
      value: c.failed,
    },
    {
      key: 'unlisted',
      text: t('import.left.unlisted', { count: n(c.unlisted) }, c.unlisted),
      value: c.unlisted,
    },
  ].filter((line) => line.value > 0)
})

// Read out when it changes, so that someone who cannot see the page hears how the import is going.
const announcement = computed(() => {
  switch (phase.value) {
    case 'uploading':
      return t('import.upload.title', { name: upload.value?.name ?? '' })
    case 'running':
      return t('import.running.title', { name: run.value?.fileName ?? '' })
    case 'ended':
      return run.value?.state === 'failed'
        ? t('import.failed.title')
        : entries.value.length > 0
          ? t('import.done.titleWithNotes')
          : t('import.done.title')
    default:
      return ''
  }
})

const statusBadge: Record<ReportStatus, string> = {
  imperfect: 'bg-tertiary-container text-on-tertiary-container',
  conflict: 'bg-secondary-container text-on-secondary-container',
  skipped: 'bg-surface-container-high text-on-surface',
  failed: 'bg-error-container text-on-error-container',
}

function choose() {
  input.value?.click()
}

async function onChosen(event: Event) {
  const element = event.target as HTMLInputElement
  const file = element.files?.[0]
  // Cleared, so that choosing the same file again (after a failure, say) still counts as a change.
  element.value = ''
  const workspaceId = tree.workspace?.id
  if (!file || !workspaceId) return

  uploadError.value = null
  controller = new AbortController()
  upload.value = { name: file.name, sent: 0, total: file.size }
  try {
    const { id } = await startJoplinImport(
      workspaceId,
      file,
      (sent, total) => {
        if (upload.value) upload.value = { ...upload.value, sent, total }
      },
      controller.signal,
    )
    shown.value = pageSize
    upload.value = null
    await router.replace({ name: 'import', params: { importId: id } })
  } catch (e) {
    // A cancelled upload is not an error.
    if (!(e instanceof DOMException && e.name === 'AbortError')) {
      uploadError.value =
        e instanceof ApiError
          ? {
              code: e.code,
              retryAfterSeconds: e.retryAfterSeconds,
              runningId: e instanceof ImportInProgressError ? (e.importId ?? undefined) : undefined,
            }
          : { code: 'unknown' }
    }
  } finally {
    upload.value = null
    controller = null
  }
}

function cancelUpload() {
  controller?.abort()
}

function another() {
  uploadError.value = null
  void router.push({ name: 'import' })
}

const button =
  'inline-flex min-h-11 items-center justify-center gap-space-xs rounded border border-border-strong px-space-lg text-label-md hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary md:min-h-9'
const primaryButton =
  'inline-flex min-h-11 items-center justify-center gap-space-xs rounded bg-primary-container px-space-lg text-label-md text-on-primary-container hover:opacity-90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary md:min-h-9'
</script>

<template>
  <section
    class="mx-auto flex max-w-3xl flex-col gap-space-lg p-margin md:p-margin-desktop"
    data-testid="import-page"
    :data-phase="phase"
  >
    <header class="flex flex-col gap-space-xs">
      <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('import.title') }}</h1>
      <p class="text-body-sm text-on-surface-variant">{{ t('import.intro') }}</p>
    </header>

    <p role="status" class="sr-only">{{ announcement }}</p>

    <!-- Before the upload -->
    <template v-if="phase === 'start'">
      <p
        v-if="!tree.canEdit"
        role="alert"
        class="rounded border border-border-strong bg-surface-panel p-space-lg text-body-sm"
      >
        {{ t('import.viewerOnly') }}
      </p>

      <template v-else>
        <div
          class="flex flex-col gap-space-sm rounded border border-border-subtle bg-surface-panel p-space-lg"
        >
          <h2 class="text-headline-sm">{{ t('import.export.title') }}</h2>
          <ol class="list-decimal ps-space-xl text-body-sm text-on-surface-variant">
            <li>{{ t('import.export.step1') }}</li>
            <li>{{ t('import.export.step2') }}</li>
            <li>{{ t('import.export.step3') }}</li>
          </ol>
          <p class="text-body-sm text-on-surface-variant">{{ t('import.export.again') }}</p>
        </div>

        <div class="flex flex-col items-start gap-space-sm">
          <input
            ref="input"
            type="file"
            class="sr-only"
            tabindex="-1"
            :aria-label="t('import.choose.label')"
            data-testid="import-file"
            @change="onChosen"
          />
          <button type="button" :class="primaryButton" data-testid="import-choose" @click="choose">
            <AppIcon name="upload" />
            {{ t('import.choose.button') }}
          </button>
          <p v-if="maxFileSize !== null" class="text-body-sm text-on-surface-variant">
            {{ t('import.choose.limit', { size: formatFileSize(maxFileSize, locale) }) }}
          </p>
        </div>

        <div
          v-if="uploadError"
          role="alert"
          class="flex flex-col items-start gap-space-sm rounded border border-error bg-error-container p-space-lg text-body-sm text-on-error-container"
        >
          <p data-testid="import-error">
            {{ errorMessage(uploadError.code, uploadError.retryAfterSeconds) }}
          </p>
          <RouterLink
            v-if="uploadError.runningId"
            :to="{ name: 'import', params: { importId: uploadError.runningId } }"
            class="underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-on-error-container"
            data-testid="import-show-running"
          >
            {{ t('import.showRunning') }}
          </RouterLink>
        </div>
      </template>
    </template>

    <!-- During the upload -->
    <div
      v-else-if="phase === 'uploading' && upload"
      class="flex flex-col gap-space-md rounded border border-border-subtle bg-surface-panel p-space-lg"
      data-testid="import-uploading"
    >
      <h2 class="break-words text-headline-sm">
        {{ t('import.upload.title', { name: upload.name }) }}
      </h2>
      <ProgressBar
        :label="t('import.upload.label')"
        :value="upload.sent"
        :max="Math.max(upload.total, 1)"
      />
      <p class="text-body-sm text-on-surface-variant">
        {{
          upload.sent >= upload.total
            ? t('import.upload.checking')
            : t('import.upload.progress', {
                sent: formatFileSize(upload.sent, locale),
                total: formatFileSize(upload.total, locale),
              })
        }}
      </p>
      <div>
        <button type="button" :class="button" @click="cancelUpload">
          {{ t('import.cancel') }}
        </button>
      </div>
    </div>

    <!-- The import cannot be shown -->
    <div v-else-if="phase === 'unavailable'" class="flex flex-col items-start gap-space-md">
      <p role="alert" class="text-body-lg" data-testid="import-unavailable">
        {{ errorMessage(error ?? 'unknown') }}
      </p>
      <button type="button" :class="button" @click="another">{{ t('import.another') }}</button>
    </div>

    <div v-else-if="phase === 'loading'" class="flex flex-col gap-space-sm">
      <p class="text-body-lg text-on-surface-variant">{{ t('import.loading') }}</p>
      <p v-if="connectionLost" role="alert" class="text-body-sm text-on-surface-variant">
        {{ t('import.connectionLost') }}
      </p>
    </div>

    <!-- While it imports -->
    <div
      v-else-if="phase === 'running' && run"
      class="flex flex-col gap-space-md rounded border border-border-subtle bg-surface-panel p-space-lg"
      data-testid="import-running"
    >
      <h2 class="break-words text-headline-sm">
        {{ t('import.running.title', { name: run.fileName }) }}
      </h2>
      <ProgressBar
        :label="t('import.running.label')"
        :value="run.state === 'running' && run.totalItems > 0 ? run.processedItems : null"
        :max="Math.max(run.totalItems, 1)"
      />
      <p class="text-body-sm text-on-surface-variant">
        {{
          run.state === 'queued'
            ? t('import.running.queued')
            : t('import.running.progress', {
                processed: n(run.processedItems),
                total: n(run.totalItems),
              })
        }}
      </p>
      <p v-if="connectionLost" role="alert" class="text-body-sm text-on-surface-variant">
        {{ t('import.connectionLost') }}
      </p>
      <p class="text-body-sm text-on-surface-variant">{{ t('import.running.leave') }}</p>
    </div>

    <!-- At the end: the report -->
    <template v-else-if="phase === 'ended' && run">
      <div class="flex flex-col gap-space-xs">
        <h2 class="text-headline-lg" data-testid="import-result">{{ announcement }}</h2>
        <p class="break-words text-body-sm text-on-surface-variant">{{ run.fileName }}</p>
      </div>

      <div
        v-if="run.state === 'failed'"
        role="alert"
        class="flex flex-col gap-space-xs rounded border border-error bg-error-container p-space-lg text-on-error-container"
        data-testid="import-failure"
      >
        <p class="text-body-lg">{{ errorMessage(run.errorCode ?? 'import_failed') }}</p>
        <p class="text-body-sm">{{ t('import.failed.keep') }}</p>
      </div>

      <section :aria-label="t('import.counts.title')">
        <h3 class="mb-space-sm text-headline-sm">{{ t('import.counts.title') }}</h3>
        <dl class="grid grid-cols-2 gap-space-sm md:grid-cols-3">
          <div
            v-for="count in counts"
            :key="count.key"
            class="rounded border border-border-subtle bg-surface-panel p-space-md"
            :data-testid="`import-count-${count.key}`"
          >
            <dt class="text-label-xs text-on-surface-variant">{{ count.label }}</dt>
            <dd class="text-headline-lg">{{ n(count.value) }}</dd>
          </div>
        </dl>
      </section>

      <ul
        v-if="leftAlone.length > 0"
        class="flex flex-col gap-space-xs text-body-sm text-on-surface-variant"
        data-testid="import-left"
      >
        <li v-for="line in leftAlone" :key="line.key">{{ line.text }}</li>
      </ul>

      <section :aria-label="t('import.report.title')" class="flex flex-col gap-space-sm">
        <h3 class="text-headline-sm">{{ t('import.report.title') }}</h3>
        <p v-if="entries.length === 0" class="text-body-sm text-on-surface-variant">
          {{ t('import.report.empty') }}
        </p>
        <template v-else>
          <p class="text-body-sm text-on-surface-variant">
            {{ t('import.report.intro', { count: n(entries.length) }, entries.length) }}
          </p>
          <ul class="flex flex-col gap-space-sm" data-testid="import-report">
            <li
              v-for="entry in visibleEntries"
              :key="`${entry.kind}:${entry.externalId}`"
              class="flex flex-col gap-space-xs rounded border border-border-subtle bg-surface-panel p-space-md"
              :data-status="entry.status"
            >
              <div class="flex flex-wrap items-center gap-space-sm">
                <span
                  class="rounded-full px-space-sm text-label-xs"
                  :class="statusBadge[entry.status] ?? statusBadge.imperfect"
                >
                  {{ messages.status(entry.status) }}
                </span>
                <span class="text-label-xs text-on-surface-variant">{{
                  messages.kind(entry.kind)
                }}</span>
              </div>
              <p class="break-words text-body-lg">
                <RouterLink
                  v-if="entry.pageId"
                  :to="{ name: 'page', params: { pageId: entry.pageId } }"
                  class="text-primary underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
                >
                  {{ entry.title || t('tree.untitled') }}
                </RouterLink>
                <template v-else>{{ entry.title || t('tree.untitled') }}</template>
              </p>
              <ul
                v-if="entry.reasons.length > 0"
                class="list-disc ps-space-xl text-body-sm text-on-surface-variant"
              >
                <li v-for="(reason, index) in entry.reasons" :key="index">
                  {{ messages.reason(reason) }}
                </li>
              </ul>
            </li>
          </ul>
          <div v-if="entries.length > shown">
            <button type="button" :class="button" @click="shown += pageSize">
              {{ t('import.report.showMore', { count: n(nextBatch) }) }}
            </button>
          </div>
        </template>
      </section>

      <div class="flex flex-wrap gap-space-sm">
        <RouterLink :to="{ name: 'home' }" :class="primaryButton">{{
          t('import.toPages')
        }}</RouterLink>
        <button v-if="tree.canEdit" type="button" :class="button" @click="another">
          {{ t('import.another') }}
        </button>
      </div>
    </template>
  </section>
</template>
