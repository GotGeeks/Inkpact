<script setup lang="ts">
import {
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
  ListboxContent,
  ListboxFilter,
  ListboxItem,
  ListboxRoot,
} from 'reka-ui'
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import AppIcon from '../components/AppIcon.vue'
import { useTreeStore } from '../stores/tree'
import { isAllowedLink, normalizeLinkHref, pageHref } from './links'
import { findPages, sharedTitles, type PageChoice } from './pageSearch'

const props = defineProps<{
  open: boolean
  /** The page being edited: not offered, a link to itself being no use. */
  pageId: string
  /** The text already is a link: the picker can take it away. */
  hasLink: boolean
}>()

const emit = defineEmits<{
  /** The address to put on the text: `/p/<id>` for a page, anything else as it is typed. */
  choose: [href: string]
  remove: []
  /** Closed without a choice (Escape, the close button, a click outside). */
  cancel: []
}>()

const { t, n } = useI18n()
const tree = useTreeStore()

const query = ref('')
// What ListboxRoot exposes, as far as this uses it.
const listbox = ref<{ highlightFirstItem: () => void } | null>(null)
const listId = 'link-picker-results'

interface Option {
  /** What the option's value is: a page's `/p/<id>`, or the address as typed. */
  value: string
  kind: 'address' | 'page'
  title: string
  parentTitle: string | null
  id?: string
}

/**
 * What the picker offers for some text: the address first when what was typed is one the editor allows (`http`, `https`,
 * `mailto`, or a page of this app), as it is what was typed, then the pages whose title contains it.
 */
function optionsFor(text: string): { options: Option[]; pages: PageChoice[] } {
  const typed = text.trim()
  const pages = findPages(tree.pages, typed, props.pageId)
  const address: Option[] =
    typed !== '' && isAllowedLink(typed)
      ? [{ value: normalizeLinkHref(typed), kind: 'address', title: typed, parentTitle: null }]
      : []
  return {
    pages,
    options: [
      ...address,
      ...pages.map((page) => ({
        value: pageHref(page.id),
        kind: 'page' as const,
        title: page.title,
        parentTitle: page.parentTitle,
        id: page.id,
      })),
    ],
  }
}

// Nothing is worked out while the picker is closed: it is always mounted, and the pages of a workspace can be many.
const offered = computed(() =>
  props.open ? optionsFor(query.value) : { options: [] as Option[], pages: [] as PageChoice[] },
)
const options = computed(() => offered.value.options)
const shared = computed(() => sharedTitles(offered.value.pages))

// A new open starts from nothing typed, and the first option is the one Enter takes.
watch(
  () => props.open,
  (open) => {
    if (open) query.value = ''
  },
)
/**
 * What was in the field when the highlight last followed the list. While the field has something else (a key that changed the
 * text, before the list and its highlight caught up), the highlighted option is one of the list before, or none, and Enter must
 * not take it. A key that leaves the text as it was (the same letter typed over a selection) changes nothing, and the
 * person's own highlight stays good.
 */
let listedFor = ''
watch(
  [options, query],
  async () => {
    await nextTick()
    listbox.value?.highlightFirstItem()
    listedFor = query.value
  },
  { flush: 'post' },
)

function choose(option: Option) {
  emit('choose', option.value)
}

/**
 * The first option is highlighted a moment after the list changes, and Reka takes Enter for the highlighted one. An Enter before
 * that (a quick typist, a paste), while the highlight is still on an option of the list before, or on none because the options
 * were drawn again after it was asked for, would take a wrong option or nothing. It chooses the first option of what is in the
 * field instead, which is where the highlight is about to be. With an option highlighted by the person, Reka's own handler does it.
 */
function onEnter(event: KeyboardEvent) {
  const field = event.currentTarget as HTMLInputElement
  const highlighted = field
    .closest('[data-testid="link-picker"]')
    ?.querySelector('[data-highlighted]')
  if (field.value === listedFor && highlighted) return
  const first = optionsFor(field.value).options[0]
  event.preventDefault()
  // Reka's own handler, on the same field, would take the option that is highlighted now.
  event.stopImmediatePropagation()
  if (first) choose(first)
}

function update(open: boolean) {
  if (!open) emit('cancel')
}

/** The field has the focus when the dialog opens; the editor gets its own back when the answer is in (see PageEditor). */
function focusInput(event: Event) {
  event.preventDefault()
  const input = (event.currentTarget as HTMLElement | null)?.querySelector('input')
  input?.focus()
}

function keepFocusForEditor(event: Event) {
  // Nothing opened this dialog to return the focus to: the editor puts it back where the text is.
  event.preventDefault()
}
</script>

<template>
  <DialogRoot :open="open" @update:open="update">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 z-40 bg-surface-canvas/70" />
      <DialogContent
        class="fixed inset-0 z-50 flex flex-col bg-surface-panel text-on-surface outline-none md:inset-auto md:start-1/2 md:top-[10dvh] md:max-h-[70dvh] md:w-[min(32rem,calc(100vw-2rem))] md:-translate-x-1/2 md:rounded-lg md:border md:border-border-subtle md:shadow-lg"
        data-testid="link-picker"
        data-editor-chrome
        @open-auto-focus="focusInput"
        @close-auto-focus="keepFocusForEditor"
      >
        <DialogTitle class="sr-only">{{ t('editor.linkPicker.title') }}</DialogTitle>
        <DialogDescription class="sr-only">{{
          t('editor.linkPicker.description')
        }}</DialogDescription>

        <ListboxRoot ref="listbox" highlight-on-hover loop class="flex min-h-0 flex-1 flex-col">
          <div class="flex items-center gap-space-sm border-b border-border-subtle px-space-md">
            <AppIcon name="link" class="size-5 text-on-surface-variant" />
            <ListboxFilter
              v-model="query"
              role="combobox"
              :aria-label="t('editor.linkPicker.field')"
              :placeholder="t('editor.linkPicker.field')"
              :aria-expanded="options.length > 0"
              :aria-controls="listId"
              aria-autocomplete="list"
              autocomplete="off"
              autocapitalize="off"
              spellcheck="false"
              enterkeyhint="done"
              class="min-h-14 min-w-0 flex-1 bg-transparent text-body-lg text-on-surface outline-none placeholder:text-on-surface-variant"
              data-testid="link-picker-input"
              @keydown.enter.capture="onEnter"
            />
            <button
              type="button"
              :aria-label="t('search.close')"
              class="flex size-11 shrink-0 items-center justify-center rounded text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-primary md:size-9"
              @click="emit('cancel')"
            >
              <AppIcon name="close" class="size-5" />
            </button>
          </div>

          <div class="min-h-0 flex-1 overflow-y-auto p-space-sm">
            <!-- Always there, also empty, so that the field's aria-controls points at it. -->
            <ListboxContent
              :id="listId"
              :aria-label="t('editor.linkPicker.pages')"
              class="flex flex-col"
              data-testid="link-picker-results"
            >
              <ListboxItem
                v-for="option in options"
                :key="option.value"
                :value="option.value"
                :data-link-kind="option.kind"
                class="flex min-h-11 cursor-pointer items-center gap-space-md rounded px-space-md py-space-sm text-start text-on-surface outline-none data-highlighted:bg-surface-container-high"
                @select="choose(option)"
              >
                <AppIcon
                  :name="option.kind === 'address' ? 'link' : 'page'"
                  class="size-5 shrink-0"
                />
                <span class="flex min-w-0 flex-col">
                  <span class="truncate text-body-lg">
                    <template v-if="option.kind === 'address'">{{
                      t('editor.linkPicker.webAddress', { address: option.title })
                    }}</template>
                    <template v-else-if="option.title">{{ option.title }}</template>
                    <span v-else class="text-on-surface-variant italic">{{
                      t('tree.untitled')
                    }}</span>
                  </span>
                  <span
                    v-if="option.id && shared.has(option.id) && option.parentTitle"
                    class="truncate text-label-md text-on-surface-variant"
                  >
                    {{ t('editor.pageLink.in', { parent: option.parentTitle }) }}
                  </span>
                </span>
              </ListboxItem>
            </ListboxContent>

            <p
              v-if="options.length === 0"
              class="p-space-md text-body-sm text-on-surface-variant"
              data-testid="link-picker-empty"
            >
              {{ t('editor.linkPicker.empty') }}
            </p>
          </div>

          <div v-if="hasLink" class="border-t border-border-subtle p-space-sm">
            <button
              type="button"
              data-testid="link-picker-remove"
              class="min-h-11 rounded px-space-md text-body-sm text-error hover:bg-surface-container-high focus-visible:outline-2 focus-visible:outline-primary md:min-h-9"
              @click="emit('remove')"
            >
              {{ t('editor.linkPicker.remove') }}
            </button>
          </div>
        </ListboxRoot>

        <!-- What a screen reader hears when the list changes. -->
        <p role="status" class="sr-only">
          {{
            options.length
              ? t('editor.linkPicker.results', { count: n(options.length) }, options.length)
              : t('editor.linkPicker.empty')
          }}
        </p>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
