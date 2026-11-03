<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { useTreeStore } from '../stores/tree'
import { groupByParent } from '../tree/treeModel'

const props = defineProps<{ pageId: string }>()

const { t, n } = useI18n()
const tree = useTreeStore()

// What the sidebar shows, so the list follows every change to the tree at once.
const groups = computed(() => groupByParent(tree.pages))
/** The live pages right under this one, in the sidebar's order: a notebook's notes, or a page's sub-pages. */
const children = computed(() => groups.value.get(props.pageId) ?? [])
const countOf = (id: string) => groups.value.get(id)?.length ?? 0
</script>

<template>
  <nav v-if="children.length > 0" :aria-labelledby="`children-${pageId}`" data-testid="child-pages">
    <h2
      :id="`children-${pageId}`"
      class="mb-space-xs text-label-md text-on-surface-variant uppercase"
    >
      {{ t('page.children') }}
    </h2>
    <ul class="flex flex-col">
      <li v-for="child in children" :key="child.id">
        <RouterLink
          :to="{ name: 'page', params: { pageId: child.id } }"
          class="flex min-h-11 items-center gap-space-sm rounded px-space-sm text-body-lg hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
        >
          <span class="min-w-0 flex-1 truncate">{{ child.title || t('tree.untitled') }}</span>
          <!-- A page with pages of its own (a notebook) says how many, which is how it is told from a note here. -->
          <span v-if="countOf(child.id) > 0" class="shrink-0 text-label-md text-on-surface-variant">
            {{ t('page.childCount', { count: n(countOf(child.id)) }, countOf(child.id)) }}
          </span>
        </RouterLink>
      </li>
    </ul>
  </nav>
</template>
