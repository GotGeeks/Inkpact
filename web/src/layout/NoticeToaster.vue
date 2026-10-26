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
import { useErrorMessage } from '../errors'
import { useNotificationsStore, type PostedNotice } from '../stores/notifications'
import { useTreeStore } from '../stores/tree'
import AppIcon from '../components/AppIcon.vue'

const { t } = useI18n()
const message = useErrorMessage()
const notifications = useNotificationsStore()
const tree = useTreeStore()

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
  }
}

async function undo(notice: PostedNotice) {
  if (notice.type === 'trashed') await tree.restore(notice.pageId)
}
</script>

<template>
  <ToastProvider label="" swipe-direction="right">
    <ToastRoot
      v-for="notice in notifications.notices"
      :key="notice.id"
      :duration="notice.type === 'error' ? 8000 : 6000"
      :type="notice.type === 'error' ? 'foreground' : 'background'"
      class="flex items-center gap-space-md rounded-md border border-border-subtle bg-surface-elevated p-space-md text-body-sm text-on-surface shadow-lg"
      :class="notice.type === 'error' ? 'border-error' : ''"
      @update:open="(open: boolean) => !open && notifications.dismiss(notice.id)"
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
      <ToastClose
        :aria-label="t('notices.dismiss')"
        class="flex size-8 items-center justify-center rounded text-on-surface-variant hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary"
      >
        <AppIcon name="close" />
      </ToastClose>
    </ToastRoot>
    <ToastViewport
      class="fixed inset-x-space-md bottom-space-md z-[60] m-0 flex list-none flex-col gap-space-sm p-0 outline-none md:inset-x-auto md:end-space-lg md:w-96"
    />
  </ToastProvider>
</template>
