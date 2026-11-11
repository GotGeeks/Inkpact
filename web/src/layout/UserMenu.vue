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
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import AppIcon from '../components/AppIcon.vue'
import { api, ApiError } from '../api'
import { applyLocale, locales, type Locale } from '../i18n'
import { applyTheme, detectTheme, isTheme, themes, type Theme } from '../theme'
import { useAuthStore } from '../stores/auth'
import { useNotificationsStore } from '../stores/notifications'
import { useSettingsStore } from '../stores/settings'
import { useOfflineStore } from '../stores/offline'
import { useSyncStore } from '../stores/sync'
import { useTreeStore } from '../stores/tree'

const emit = defineEmits<{ navigate: [] }>()

const { t, locale } = useI18n()
const auth = useAuthStore()
const settings = useSettingsStore()
const tree = useTreeStore()
const notifications = useNotificationsStore()
const sync = useSyncStore()
const offline = useOfflineStore()
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

const themeNames = computed(() => ({
  system: t('theme.system'),
  light: t('theme.light'),
  dark: t('theme.dark'),
}))
// Per device, like the language: kept in this browser, System by default.
const theme = ref<Theme>(detectTheme())

function chooseTheme(next: unknown) {
  if (!isTheme(next)) return
  theme.value = next
  applyTheme(next)
}

async function openImport() {
  // On a phone the menu sits in the drawer, which has to make way for the page.
  emit('navigate')
  await router.push({ name: 'import' })
}

async function leave(everywhere: boolean) {
  if (sync.unsafeToLeave) {
    notifications.post({ type: 'error', code: 'offline_storage_pending' })
    return
  }
  const userId = auth.user?.id
  try {
    if (everywhere) await auth.logoutEverywhere()
    else await auth.logout()
  } catch (error) {
    notifications.post({ type: 'error', code: error instanceof ApiError ? error.code : 'unknown' })
    if (auth.user) return
  }
  // Signed out: the pages the server has leave this device too. Best effort, as the person is already signed out.
  if (userId) {
    await offline
      .forgetAccount(userId)
      .catch((error: unknown) => console.error('Cached pages could not be removed', error))
  }
  await router.replace({ name: 'login' })
}

/**
 * The zip is a download the browser makes itself (with the session cookie; a GET needs no CSRF token), so the app never
 * sees its answer. The server is asked first whether it can be made, so that an ended session or a server that cannot read
 * pages right now is a message in the person's language and not a failed download. The README inside is in the language
 * shown here.
 */
async function exportEverything() {
  const workspaceId = tree.workspace?.id
  if (!workspaceId) return
  try {
    await api('GET', `/api/workspaces/${workspaceId}/export/check`)
  } catch (error) {
    notifications.post({ type: 'error', code: error instanceof ApiError ? error.code : 'unknown' })
    return
  }

  const link = document.createElement('a')
  link.href = `/api/workspaces/${workspaceId}/export?lang=${locale.value}`
  link.download = ''
  document.body.append(link)
  link.click()
  link.remove()
}

const item =
  'flex min-h-11 cursor-pointer items-center rounded px-space-md text-body-sm outline-none data-[highlighted]:bg-surface-container-high md:min-h-8'
</script>

<template>
  <DropdownMenuRoot>
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        :aria-label="t('user.menu', { name: auth.user?.displayName })"
        class="flex min-h-11 w-full items-center gap-space-sm rounded px-space-md text-start text-body-sm text-on-surface hover:bg-surface-elevated focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary md:min-h-9"
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
        <DropdownMenuLabel class="px-space-md py-space-xs text-label-xs text-on-surface-variant">{{
          t('theme.label')
        }}</DropdownMenuLabel>
        <DropdownMenuRadioGroup :model-value="theme" @update:model-value="chooseTheme">
          <DropdownMenuRadioItem
            v-for="choice in themes"
            :key="choice"
            :value="choice"
            :class="[item, 'justify-between']"
          >
            {{ themeNames[choice] }}
            <AppIcon v-if="theme === choice" name="check" class="text-primary" />
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
        <DropdownMenuSeparator class="my-space-xs h-px bg-border-subtle" />
        <DropdownMenuItem v-if="tree.canEdit" :class="item" @select="openImport">{{
          t('import.menu')
        }}</DropdownMenuItem>
        <DropdownMenuSeparator v-if="tree.canEdit" class="my-space-xs h-px bg-border-subtle" />
        <template v-if="tree.workspace">
          <!-- The item's name is its visible text; the hint under it is a label of the menu and describes the item. -->
          <DropdownMenuItem
            aria-describedby="export-hint"
            data-testid="export-item"
            :class="item"
            @select="exportEverything"
            >{{ t('export.menu') }}</DropdownMenuItem
          >
          <DropdownMenuLabel
            id="export-hint"
            class="px-space-md pb-space-xs text-label-xs text-on-surface-variant"
            >{{ t('export.hint') }}</DropdownMenuLabel
          >
          <DropdownMenuSeparator class="my-space-xs h-px bg-border-subtle" />
        </template>
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
