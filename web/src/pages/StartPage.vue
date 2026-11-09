<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, useRouter } from 'vue-router'
import { formatAgo } from '../relativeTime'
import { useRecentStore } from '../stores/recent'
import { useTreeStore } from '../stores/tree'

/** The start page lists this many of the pages opened last. */
const shown = 10
/** How often "5 minutes ago" is worked out again while the page stays open. */
const tick = 30_000

const { t, locale } = useI18n()
const tree = useTreeStore()
const recent = useRecentStore()
const router = useRouter()

// The clock the relative times are measured against, moved on while the page is open.
const now = ref(new Date())
let timer: ReturnType<typeof setInterval> | undefined

// Titles come from the tree, so a rename shows at once. A page the tree does not have (made on another device since it
// was read) is left out until the tree is read again.
const items = computed(() =>
  recent.items
    .flatMap((item) => {
      const page = tree.byId.get(item.pageId)
      return page ? [{ page, openedAt: item.openedAt }] : []
    })
    .slice(0, shown)
    .map(({ page, openedAt }) => ({
      id: page.id,
      title: page.title || t('tree.untitled'),
      parentTitle: page.parentId ? tree.byId.get(page.parentId)?.title || t('tree.untitled') : null,
      ago: formatAgo(openedAt, locale.value, now.value),
    })),
)

onMounted(() => {
  void recent.load()
  recent.watchFocus()
  timer = setInterval(() => (now.value = new Date()), tick)
})
onBeforeUnmount(() => {
  recent.unwatchFocus()
  clearInterval(timer)
})

async function create() {
  const change = await tree.create('')
  if (change) await router.push({ name: 'page', params: { pageId: change.page.id } })
}
</script>

<template>
  <section class="mx-auto flex max-w-2xl flex-col gap-space-lg p-margin md:p-margin-desktop">
    <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('start.title') }}</h1>
    <p class="text-body-lg text-on-surface-variant">
      {{ tree.pages.length === 0 ? t('start.empty') : t('start.pick') }}
    </p>

    <section v-if="items.length > 0" aria-labelledby="recent-heading" data-testid="recent-pages">
      <h2 id="recent-heading" class="mb-space-xs text-label-md text-on-surface-variant uppercase">
        {{ t('start.recent') }}
      </h2>
      <ul class="flex flex-col">
        <li v-for="item in items" :key="item.id">
          <RouterLink
            :to="{ name: 'page', params: { pageId: item.id } }"
            class="flex min-h-11 items-center gap-space-sm rounded px-space-sm text-body-lg hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
          >
            <span class="min-w-0 flex-1">
              <span class="block truncate">{{ item.title }}</span>
              <span
                v-if="item.parentTitle"
                class="block truncate text-label-md text-on-surface-variant"
                >{{ t('start.recentIn', { parent: item.parentTitle }) }}</span
              >
            </span>
            <span class="shrink-0 text-label-md text-on-surface-variant">{{ item.ago }}</span>
          </RouterLink>
        </li>
      </ul>
    </section>

    <button
      v-if="tree.canEdit"
      type="button"
      class="min-h-11 self-start rounded bg-primary-container px-space-lg text-label-md text-on-primary-container hover:opacity-90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary md:min-h-9"
      @click="create"
    >
      {{ t('tree.newPage') }}
    </button>
  </section>
</template>
