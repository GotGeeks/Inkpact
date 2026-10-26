<script setup lang="ts">
import {
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useTreeStore } from '../stores/tree'
import { isNoMove, moveTargets } from './treeModel'

const props = defineProps<{ pageId: string | null }>()
const emit = defineEmits<{ close: [] }>()

const { t } = useI18n()
const tree = useTreeStore()

const page = computed(() => (props.pageId ? tree.byId.get(props.pageId) : undefined))
const targets = computed(() => (props.pageId ? moveTargets(tree.pages, props.pageId) : []))

const indent = [
  'ps-3',
  'ps-7',
  'ps-11',
  'ps-15',
  'ps-19',
  'ps-23',
  'ps-27',
  'ps-31',
  'ps-35',
  'ps-39',
] as const
const indentClass = (level: number) => indent[Math.min(level - 1, indent.length - 1)]

async function moveTo(parentId: string | null) {
  const id = props.pageId
  emit('close')
  if (!id) return
  const position = { parentId }
  // Moving to where it already is would only raise its version for nothing.
  if (!isNoMove(tree.pages, id, position)) await tree.move(id, position)
}

const option =
  'flex min-h-11 w-full items-center rounded px-space-md text-start text-body-sm text-on-surface outline-none hover:bg-surface-container-high focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary md:min-h-8'
</script>

<template>
  <DialogRoot :open="pageId !== null" @update:open="(open: boolean) => !open && emit('close')">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-40 bg-surface-canvas/70" />
      <DialogContent
        class="fixed start-1/2 top-1/2 z-50 flex max-h-[80dvh] w-[min(28rem,calc(100vw-2rem))] -translate-x-1/2 -translate-y-1/2 flex-col gap-space-md rounded-lg border border-border-subtle bg-surface-panel p-space-lg text-on-surface shadow-lg"
      >
        <DialogTitle class="text-headline-sm">{{
          t('tree.moveDialog.title', { title: page?.title || t('tree.untitled') })
        }}</DialogTitle>
        <DialogDescription class="text-body-sm text-on-surface-variant">{{
          t('tree.moveDialog.description')
        }}</DialogDescription>

        <ul class="min-h-0 flex-1 overflow-y-auto" role="list">
          <li>
            <button
              type="button"
              :class="[option, page?.parentId === null ? 'text-on-surface-variant' : '']"
              @click="moveTo(null)"
            >
              {{ t('tree.moveDialog.topLevel') }}
            </button>
          </li>
          <li v-for="row in targets" :key="row.page.id">
            <button
              type="button"
              :class="[option, indentClass(row.level)]"
              @click="moveTo(row.page.id)"
            >
              <span
                class="truncate"
                :class="row.page.title ? '' : 'text-on-surface-variant italic'"
                >{{ row.page.title || t('tree.untitled') }}</span
              >
            </button>
          </li>
        </ul>

        <button
          type="button"
          class="min-h-11 self-end rounded border border-border-subtle px-space-lg text-label-md hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-container md:min-h-9"
          @click="emit('close')"
        >
          {{ t('tree.moveDialog.cancel') }}
        </button>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
