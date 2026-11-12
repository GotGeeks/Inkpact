<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import { useFavoritesStore } from '../stores/favorites'

const props = defineProps<{
  pageId: string
  /** A page in the trash cannot be starred, though a star it has can be taken off. */
  inTrash?: boolean
}>()

const { t } = useI18n()
const favorites = useFavoritesStore()
const starred = computed(() => favorites.has(props.pageId))
</script>

<template>
  <!-- The name stays the same; the state is said by aria-pressed. -->
  <button
    type="button"
    :aria-label="t('favorites.toggle')"
    :aria-pressed="starred"
    :disabled="inTrash && !starred"
    data-testid="favorite-toggle"
    :class="[
      'flex size-11 shrink-0 items-center justify-center rounded hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary disabled:opacity-60 md:size-9',
      starred ? 'text-primary [&_svg]:fill-current' : 'text-on-surface-variant',
    ]"
    @click="favorites.toggle(pageId)"
  >
    <AppIcon name="star" class="size-5" />
  </button>
</template>
