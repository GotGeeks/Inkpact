<script setup lang="ts">
import { nextTick } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import AppIcon from '../components/AppIcon.vue'
import { useSearchStore } from '../stores/search'
import { useTreeStore } from '../stores/tree'
import SidebarTree from '../tree/SidebarTree.vue'
import UserMenu from './UserMenu.vue'

defineProps<{ selectedId: string | null }>()
const emit = defineEmits<{ open: [id: string]; created: [id: string]; navigate: [] }>()

const { t, n } = useI18n()
const tree = useTreeStore()
const search = useSearchStore()

// The shortcut is written as the person's keyboard has it.
const isMac = /Mac|iPhone|iPad/.test(globalThis.navigator?.userAgent ?? '')

async function openSearch() {
  // A drawer closes first: two dialogs do not share the focus.
  emit('navigate')
  await nextTick()
  search.show()
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-space-md bg-surface-panel py-space-md">
    <div class="px-space-md">
      <p class="text-label-xs text-on-surface-variant">{{ t('app.name') }}</p>
      <p class="truncate text-headline-sm" data-testid="workspace-name">
        {{ tree.workspace?.name }}
      </p>
    </div>

    <div class="px-space-xs">
      <button
        type="button"
        class="flex min-h-11 w-full items-center gap-space-sm rounded border border-border-subtle px-space-md text-body-sm text-on-surface-variant hover:bg-surface-elevated focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary md:min-h-9"
        data-testid="search-row"
        @click="openSearch"
      >
        <AppIcon name="search" />
        <span class="flex-1 text-start">{{ t('search.link') }}</span>
        <kbd class="hidden text-label-xs md:inline" aria-hidden="true">{{
          isMac ? t('search.shortcutMac') : t('search.shortcutOther')
        }}</kbd>
      </button>
    </div>

    <SidebarTree
      :selected-id="selectedId"
      @open="emit('open', $event)"
      @created="emit('created', $event)"
    />

    <nav class="px-space-xs" :aria-label="t('tags.navLabel')">
      <RouterLink
        :to="{ name: 'tags' }"
        class="flex min-h-11 items-center gap-space-sm rounded px-space-md text-body-sm text-on-surface-variant hover:bg-surface-elevated aria-[current=page]:bg-surface-elevated aria-[current=page]:text-on-surface focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary md:min-h-8"
        @click="emit('navigate')"
      >
        <AppIcon name="tag" />
        <span class="flex-1">{{ t('tags.link') }}</span>
      </RouterLink>
    </nav>

    <nav class="px-space-xs" :aria-label="t('trash.navLabel')">
      <RouterLink
        :to="{ name: 'trash' }"
        class="flex min-h-11 items-center gap-space-sm rounded px-space-md text-body-sm text-on-surface-variant hover:bg-surface-elevated aria-[current=page]:bg-surface-elevated aria-[current=page]:text-on-surface focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary md:min-h-8"
        @click="emit('navigate')"
      >
        <AppIcon name="trash" />
        <span class="flex-1">{{ t('trash.link') }}</span>
        <span
          v-if="tree.trash.length > 0"
          class="rounded-full bg-surface-container-high px-space-sm text-label-xs"
          data-testid="trash-count"
        >
          {{ n(tree.trash.length) }}
        </span>
      </RouterLink>
    </nav>

    <div class="border-t border-border-subtle px-space-xs pt-space-sm">
      <UserMenu @navigate="emit('navigate')" />
    </div>
  </div>
</template>
