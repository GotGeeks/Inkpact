<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { api } from '../api'
import { useTreeStore } from '../stores/tree'

interface Backlink {
  id: string
  title: string
  parentTitle: string | null
}

const props = defineProps<{ pageId: string }>()

const { t, n } = useI18n()
const tree = useTreeStore()

const items = ref<Backlink[]>([])
const more = ref(false)

// The newest ask wins: a slow answer for the page the person has left must not replace the page they are on.
let asked = 0
async function load() {
  const mine = ++asked
  try {
    const answer = await api<{ items: Backlink[]; more: boolean }>(
      'GET',
      `/api/pages/${encodeURIComponent(props.pageId)}/backlinks`,
    )
    if (mine !== asked) return
    items.value = answer.items
    more.value = answer.more
  } catch {
    // The links are a convenience: a page that cannot say what links to it is still a page.
    if (mine === asked) items.value = []
  }
}

// When the page opens, when the tree changes (a page that links here was trashed or restored), and when the tab comes back:
// the links are indexed by the server some minutes after they are typed, so a tab left open learns of them this way.
watch([() => props.pageId, () => tree.treeVersion], load)
onMounted(() => {
  void load()
  document.addEventListener('visibilitychange', onVisible)
})
onBeforeUnmount(() => {
  asked++
  document.removeEventListener('visibilitychange', onVisible)
})
function onVisible() {
  if (document.visibilityState === 'visible') void load()
}
</script>

<template>
  <nav
    v-if="items.length > 0"
    :aria-labelledby="`backlinks-${pageId}`"
    data-testid="backlinks"
    class="border-t border-border-subtle pt-space-lg"
  >
    <h2
      :id="`backlinks-${pageId}`"
      class="mb-space-xs text-label-md text-on-surface-variant uppercase"
    >
      {{ t('page.backlinks', { count: n(items.length) }, items.length) }}
    </h2>
    <ul class="flex flex-col">
      <li v-for="item in items" :key="item.id">
        <RouterLink
          :to="{ name: 'page', params: { pageId: item.id } }"
          class="flex min-h-11 items-center gap-space-sm rounded px-space-sm text-body-lg hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
        >
          <span class="min-w-0 flex-1 truncate">{{ item.title || t('tree.untitled') }}</span>
          <!-- Where it is, so two pages with one title can be told apart. -->
          <span
            v-if="item.parentTitle"
            class="max-w-[40%] shrink-0 truncate text-label-md text-on-surface-variant"
          >
            {{ item.parentTitle }}
          </span>
        </RouterLink>
      </li>
    </ul>
    <p v-if="more" class="px-space-sm pt-space-xs text-body-sm text-on-surface-variant">
      {{ t('page.backlinksMore') }}
    </p>
  </nav>
</template>
