<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import AppIcon from '../components/AppIcon.vue'
import { useFavoritesStore } from '../stores/favorites'
import { useTreeStore } from '../stores/tree'

defineProps<{ selectedId: string | null }>()
const emit = defineEmits<{ navigate: [] }>()

const { t } = useI18n()
const tree = useTreeStore()
const favorites = useFavoritesStore()

// Titles come from the tree, so a rename shows at once. An id the tree does not have yet (a page made on another device
// since the tree was read) is left out until the tree is read again.
const pages = computed(() =>
  favorites.ids.flatMap((id) => {
    const page = tree.byId.get(id)
    return page ? [page] : []
  }),
)
</script>

<template>
  <nav v-if="pages.length > 0" :aria-label="t('favorites.navLabel')" data-testid="favorites">
    <h2 class="px-space-md pb-space-xs text-label-xs text-on-surface-variant uppercase">
      {{ t('favorites.title') }}
    </h2>
    <ul class="max-h-40 overflow-y-auto px-space-xs md:max-h-48">
      <li v-for="page in pages" :key="page.id">
        <RouterLink
          :to="{ name: 'page', params: { pageId: page.id } }"
          :aria-current="page.id === selectedId ? 'page' : undefined"
          class="flex min-h-11 items-center gap-space-sm rounded px-space-md text-body-sm text-on-surface hover:bg-surface-elevated aria-[current=page]:bg-primary-container aria-[current=page]:text-on-primary-container focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary md:min-h-8"
          @click="emit('navigate')"
        >
          <AppIcon name="star" />
          <span class="min-w-0 flex-1 truncate">{{ page.title || t('tree.untitled') }}</span>
        </RouterLink>
      </li>
    </ul>
  </nav>
</template>
