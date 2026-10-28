<script setup lang="ts">
import {
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuPortal,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuRoot,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from 'reka-ui'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import AppIcon from '../components/AppIcon.vue'
import { applyLocale, locales, type Locale } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { useSettingsStore } from '../stores/settings'
import { useTreeStore } from '../stores/tree'

const emit = defineEmits<{ navigate: [] }>()

const { t, locale } = useI18n()
const auth = useAuthStore()
const settings = useSettingsStore()
const tree = useTreeStore()
const router = useRouter()

// Static keys, so the unused-keys lint rule can see them.
const names = computed(() => ({ en: t('language.en'), nl: t('language.nl') }))

function chooseLocale(next: unknown) {
  if (typeof next !== 'string') return
  if (!(locales as readonly string[]).includes(next)) return
  settings.setLocale(next as Locale)
  locale.value = next
  applyLocale(next as Locale)
}

async function openImport() {
  // On a phone the menu sits in the drawer, which has to make way for the page.
  emit('navigate')
  await router.push({ name: 'import' })
}

async function leave(everywhere: boolean) {
  try {
    if (everywhere) await auth.logoutEverywhere()
    else await auth.logout()
  } finally {
    await router.replace({ name: 'login' })
  }
}

const item =
  'flex min-h-11 cursor-pointer items-center rounded px-space-md text-body-sm outline-none data-[highlighted]:bg-surface-container-high md:min-h-8'
</script>

<template>
  <DropdownMenuRoot>
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        :aria-label="t('user.menu')"
        class="flex min-h-11 w-full items-center gap-space-sm rounded px-space-md text-start text-body-sm text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-container md:min-h-9"
      >
        <AppIcon name="user" />
        <span class="min-w-0 flex-1 truncate" data-testid="user-name">{{
          auth.user?.displayName
        }}</span>
        <AppIcon name="dots" />
      </button>
    </DropdownMenuTrigger>
    <DropdownMenuPortal>
      <DropdownMenuContent
        side="top"
        align="start"
        :side-offset="4"
        class="z-50 min-w-56 rounded-md border border-border-subtle bg-surface-elevated p-space-xs text-on-surface shadow-lg"
      >
        <DropdownMenuLabel class="px-space-md py-space-xs text-label-xs text-on-surface-variant">
          {{ t('user.signedInAs', { email: auth.user?.email }) }}
        </DropdownMenuLabel>
        <DropdownMenuSeparator class="my-space-xs h-px bg-border-subtle" />
        <DropdownMenuLabel class="px-space-md py-space-xs text-label-xs text-on-surface-variant">{{
          t('language.label')
        }}</DropdownMenuLabel>
        <DropdownMenuRadioGroup :model-value="locale" @update:model-value="chooseLocale">
          <DropdownMenuRadioItem
            v-for="code in locales"
            :key="code"
            :value="code"
            :class="[item, 'justify-between']"
          >
            {{ names[code] }}
            <AppIcon v-if="locale === code" name="check" class="text-primary" />
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
        <DropdownMenuSeparator class="my-space-xs h-px bg-border-subtle" />
        <DropdownMenuItem v-if="tree.canEdit" :class="item" @select="openImport">{{
          t('import.menu')
        }}</DropdownMenuItem>
        <DropdownMenuSeparator v-if="tree.canEdit" class="my-space-xs h-px bg-border-subtle" />
        <DropdownMenuItem :class="item" @select="leave(false)">{{
          t('user.logout')
        }}</DropdownMenuItem>
        <DropdownMenuItem :class="item" @select="leave(true)">{{
          t('user.logoutEverywhere')
        }}</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenuPortal>
  </DropdownMenuRoot>
</template>
