<script setup lang="ts">
// A handful of 24 px outline icons, drawn here so that nothing is fetched and no icon font is needed. The paths are
// static; none of this comes from content.
defineProps<{
  name:
    'chevron' | 'plus' | 'dots' | 'trash' | 'menu' | 'close' | 'restore' | 'page' | 'user' | 'check'
}>()

const paths = {
  chevron: 'm9 6 6 6-6 6',
  plus: 'M12 5v14M5 12h14',
  dots: 'M5 12h.01M12 12h.01M19 12h.01',
  trash: 'M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3',
  menu: 'M4 6h16M4 12h16M4 18h16',
  close: 'M6 6l12 12M18 6 6 18',
  restore: 'M4 12a8 8 0 1 0 3-6.2M4 4v4h4',
  page: 'M7 3h7l5 5v13H7zM14 3v5h5',
  check: 'm5 12 5 5 9-10',
  user: 'M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8M4 21a8 8 0 0 1 16 0',
} as const
</script>

<template>
  <svg
    aria-hidden="true"
    focusable="false"
    class="size-4 shrink-0"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2"
    stroke-linecap="round"
    stroke-linejoin="round"
  >
    <path :d="paths[name]" />
  </svg>
</template>
