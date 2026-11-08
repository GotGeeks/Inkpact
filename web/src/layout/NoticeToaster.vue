<script setup lang="ts">
import {
  ToastAction,
  ToastClose,
  ToastDescription,
  ToastProvider,
  ToastRoot,
  ToastViewport,
} from 'reka-ui'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useErrorMessage } from '../errors'
import { useNotificationsStore, type PostedNotice } from '../stores/notifications'
import { useTreeStore } from '../stores/tree'
import AppIcon from '../components/AppIcon.vue'

const { t } = useI18n()
const message = useErrorMessage()
const notifications = useNotificationsStore()
const tree = useTreeStore()
const router = useRouter()

/** The words for a notice, in the language of the page. */
function text(notice: PostedNotice): string {
  const title = notice.type === 'error' ? '' : notice.title || t('tree.untitled')
  switch (notice.type) {
    case 'error':
      return message(notice.code, notice.retryAfterSeconds)
    case 'trashed':
      return t('notices.trashed', { title })
    case 'restored':
      return notice.toTopLevel
        ? t('notices.restoredToTopLevel', { title })
        : t('notices.restored', { title })
    case 'editsKept':
      return t('notices.editsKept', { title })
    case 'editsWaiting':
      return t('notices.editsWaiting', { title })
    case 'editsTooLarge':
      return t('notices.editsTooLarge', { title })
  }
}

async function undo(notice: PostedNotice) {
  if (notice.type === 'trashed') await tree.restore(notice.pageId)
}

/** Notices that must not slip by: they say where the person's own words went. */
const IMPORTANT = new Set<PostedNotice['type']>([
  'error',
  'editsKept',
  'editsWaiting',
  'editsTooLarge',
])
const important = (notice: PostedNotice) => IMPORTANT.has(notice.type)

/**
 * Reka closes every notice on any Escape on the page, so the Escape that closes the drawer, a menu or a dialog would
 * also take the notices with it, with their Undo. Only the notice that has the focus closes.
 */
function keepUnlessFocused(notice: PostedNotice, event: KeyboardEvent) {
  const target = event.target instanceof Element ? event.target : null
  if (target?.closest('[data-notice-id]')?.getAttribute('data-notice-id') !== String(notice.id))
    event.preventDefault()
}

function openCopy(notice: PostedNotice) {
  if (notice.type === 'editsKept')
    void router.push({ name: 'page', params: { pageId: notice.pageId } })
}
</script>

<template>
  <ToastProvider label="" swipe-direction="right">
    <ToastRoot
      v-for="notice in notifications.notices"
      :key="notice.id"
      :data-notice-id="notice.id"
      :duration="notice.type === 'error' ? 8000 : important(notice) ? 30000 : 6000"
      :type="important(notice) ? 'foreground' : 'background'"
      class="flex items-center gap-space-md rounded-md border border-border-subtle bg-surface-elevated p-space-md text-body-sm text-on-surface shadow-lg"
      :class="notice.type === 'error' ? 'border-error' : ''"
      @update:open="(open: boolean) => !open && notifications.dismiss(notice.id)"
      @escape-key-down="(event: KeyboardEvent) => keepUnlessFocused(notice, event)"
    >
      <ToastDescription class="flex-1">{{ text(notice) }}</ToastDescription>
      <ToastAction
        v-if="notice.type === 'trashed'"
        as-child
        :alt-text="t('notices.undoAlt', { title: notice.title || t('tree.untitled') })"
      >
        <button
          type="button"
          class="min-h-11 rounded px-space-md text-label-md text-primary hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary md:min-h-8"
          @click="undo(notice)"
        >
          {{ t('notices.undo') }}
        </button>
      </ToastAction>
      <ToastAction
        v-if="notice.type === 'editsKept'"
        as-child
        :alt-text="t('notices.openCopyAlt', { title: notice.title || t('tree.untitled') })"
      >
        <button
          type="button"
          class="min-h-11 rounded px-space-md text-label-md text-primary hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary md:min-h-8"
          @click="openCopy(notice)"
        >
          {{ t('notices.openCopy') }}
        </button>
      </ToastAction>
      <ToastClose
        :aria-label="t('notices.dismiss')"
        class="flex size-8 pointer-coarse:size-11 items-center justify-center rounded text-on-surface-variant hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary"
      >
        <AppIcon name="close" />
      </ToastClose>
    </ToastRoot>
    <ToastViewport
      class="fixed inset-x-space-md bottom-space-md z-[60] m-0 flex list-none flex-col gap-space-sm p-0 outline-none md:inset-x-auto md:end-space-lg md:w-96"
    />
  </ToastProvider>
</template>
