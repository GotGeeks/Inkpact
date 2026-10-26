<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useTreeStore } from '../stores/tree'

const props = defineProps<{ pageId: string; title: string; readOnly: boolean }>()
const emit = defineEmits<{ enter: [] }>()

const { t } = useI18n()
const tree = useTreeStore()

// How long after the last key the title is saved. Saving on every key would raise the page's version and reload the
// tree for each one.
const SAVE_AFTER_MS = 1000

const field = ref<HTMLTextAreaElement | null>(null)
const draft = ref(props.title)
let timer: ReturnType<typeof setTimeout> | null = null

// The title changed somewhere else (a rename in the sidebar): show it, unless the person is typing here.
watch(
  () => props.title,
  (title) => {
    if (document.activeElement !== field.value) draft.value = title
  },
)

/** A title is one line. A line break typed or pasted becomes a space. */
function oneLine(value: string): string {
  return value.replace(/\s*[\r\n]+\s*/g, ' ')
}

/** Browsers without `field-sizing` leave a textarea one row high; this grows it with its text. */
function fit() {
  const el = field.value
  if (!el || CSS.supports?.('field-sizing', 'content')) return
  el.style.height = 'auto'
  el.style.height = `${el.scrollHeight}px`
}

watch(draft, () => queueMicrotask(fit))

function onInput() {
  draft.value = oneLine(draft.value)
  if (timer) clearTimeout(timer)
  timer = setTimeout(save, SAVE_AFTER_MS)
}

async function save() {
  if (timer) clearTimeout(timer)
  timer = null
  if (props.readOnly || draft.value === props.title) return
  await tree.rename(props.pageId, draft.value)
}

function onEnter() {
  void save()
  emit('enter')
}

onBeforeUnmount(() => {
  // Leaving the page with a title still waiting to be saved.
  if (timer) void save()
})

defineExpose({ focus: () => field.value?.focus() })
</script>

<template>
  <textarea
    ref="field"
    v-model="draft"
    rows="1"
    :readonly="readOnly"
    :aria-label="t('title.label')"
    :placeholder="t('tree.untitled')"
    data-testid="page-title"
    class="field-sizing-content w-full resize-none overflow-hidden bg-transparent text-headline-xl-mobile text-on-surface outline-none placeholder:text-on-surface-variant md:text-headline-xl"
    @input="onInput"
    @blur="save"
    @keydown.enter.prevent="onEnter"
  />
</template>
