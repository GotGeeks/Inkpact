<script setup lang="ts">
// A handful of 24 px outline icons, drawn here so that nothing is fetched and no icon font is needed. The paths are
// static; none of this comes from content.
defineProps<{
  name:
    | 'chevron'
    | 'plus'
    | 'dots'
    | 'trash'
    | 'menu'
    | 'close'
    | 'restore'
    | 'page'
    | 'user'
    | 'check'
    | 'text'
    | 'heading'
    | 'bulletList'
    | 'numberedList'
    | 'checklist'
    | 'quote'
    | 'code'
    | 'table'
    | 'divider'
    | 'undo'
    | 'redo'
    | 'bold'
    | 'italic'
    | 'slash'
    | 'grip'
    | 'image'
    | 'file'
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
  text: 'M5 6V5h14v1M12 5v14M9 19h6',
  heading: 'M6 5v14M18 5v14M6 12h12',
  bulletList: 'M9 6h11M9 12h11M9 18h11M4.5 6h.01M4.5 12h.01M4.5 18h.01',
  numberedList: 'M10 6h10M10 12h10M10 18h10M4 5l1.5-1v4M4 14h3l-3 3h3',
  checklist: 'M4 6l1.5 1.5L8 5M4 15l1.5 1.5L8 14M11 7h9M11 16h9',
  quote: 'M5 17v-4a4 4 0 0 1 4-4M13 17v-4a4 4 0 0 1 4-4M5 17h4v-4H5M13 17h4v-4h-4',
  code: 'm8 8-4 4 4 4M16 8l4 4-4 4M13.5 5l-3 14',
  table: 'M4 5h16v14H4zM4 10h16M4 15h16M10 5v14',
  divider: 'M4 12h16M8 7h8M8 17h8',
  undo: 'M9 14 4 9l5-5M4 9h10a6 6 0 0 1 0 12h-3',
  redo: 'm15 14 5-5-5-5M20 9H10a6 6 0 0 0 0 12h3',
  bold: 'M7 5h6a3.5 3.5 0 0 1 0 7H7zM7 12h7a3.5 3.5 0 0 1 0 7H7z',
  italic: 'M14 5h-4M14 19h-4M15 5l-6 14',
  slash: 'M16 4 8 20',
  grip: 'M9 6h.01M15 6h.01M9 12h.01M15 12h.01M9 18h.01M15 18h.01',
  image: 'M4 5h16v14H4zM4 16l5-5 4 4 2-2 5 5M15 9h.01',
  file: 'm20 11-8.5 8.5a5 5 0 0 1-7-7L13 4a3.5 3.5 0 0 1 5 5l-8.5 8.5a2 2 0 0 1-3-3L14 7',
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
