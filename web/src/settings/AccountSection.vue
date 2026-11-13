<script setup lang="ts">
import { computed, nextTick, onMounted, ref, useTemplateRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, ApiError } from '../api'
import AppButton from '../components/AppButton.vue'
import SubmitButton from '../components/SubmitButton.vue'
import TextField from '../components/TextField.vue'
import { useErrorMessage } from '../errors'
import { formatAgo } from '../relativeTime'
import { useAuthStore, type CurrentUser } from '../stores/auth'
import { deviceOf } from './deviceName'

/**
 * The person's own account (run B.9): their display name, and the browsers they are signed in on, any of which they can
 * sign out from here. This device is signed out from the account menu instead, which also clears what it kept for
 * offline use.
 */
interface SessionSummary {
  id: string
  signedInAt: string
  lastSeenAt: string
  userAgent: string | null
  current: boolean
}

const { t, locale } = useI18n()
const message = useErrorMessage()
const auth = useAuthStore()
const codeOf = (error: unknown) => (error instanceof ApiError ? error.code : 'unknown')

// ---- the display name ---------------------------------------------------------------------------------------------------

const displayName = ref(auth.user?.displayName ?? '')
const nameError = ref<string | null>(null)
const nameSaving = ref(false)
const nameSaved = ref(false)

async function saveName() {
  nameError.value = null
  nameSaved.value = false
  nameSaving.value = true
  try {
    const user = await api<CurrentUser>('PATCH', '/api/account', { displayName: displayName.value })
    auth.user = user
    displayName.value = user.displayName
    nameSaved.value = true
  } catch (error) {
    nameError.value = message(codeOf(error))
  } finally {
    nameSaving.value = false
  }
}

// ---- the devices --------------------------------------------------------------------------------------------------------

const sessions = ref<SessionSummary[] | null>(null)
const more = ref(0)
const loadError = ref<string | null>(null)
/** The device whose sign-out is being asked about, or 'others' for all the other devices. */
const confirming = ref<string | null>(null)
const busy = ref(false)
const actionError = ref<string | null>(null)
const done = ref<string | null>(null)
const listHeading = useTemplateRef<HTMLElement>('listHeading')

async function load() {
  loadError.value = null
  try {
    const answer = await api<{ sessions: SessionSummary[]; more: number }>(
      'GET',
      '/api/account/sessions',
    )
    sessions.value = answer.sessions
    more.value = answer.more
  } catch (error) {
    loadError.value = codeOf(error)
  }
}

onMounted(load)

const others = computed(() => (sessions.value ?? []).filter((session) => !session.current))

function nameOf(session: SessionSummary): string {
  const { browser, system } = deviceOf(session.userAgent)
  return t('settings.account.deviceName', {
    browser: browser ?? t('settings.account.unknownBrowser'),
    system: system ?? t('settings.account.unknownSystem'),
  })
}

const dateFormat = computed(() => new Intl.DateTimeFormat(locale.value, { dateStyle: 'medium' }))
const activity = (session: SessionSummary) =>
  t('settings.account.activity', {
    date: dateFormat.value.format(new Date(session.signedInAt)),
    when: formatAgo(session.lastSeenAt, locale.value),
  })

async function ask(which: string) {
  actionError.value = null
  done.value = null
  confirming.value = which
  await nextTick()
  document.getElementById(`confirm-${which}`)?.focus()
}

async function cancel() {
  const which = confirming.value
  confirming.value = null
  await nextTick()
  document.getElementById(which === 'others' ? 'sign-out-others' : `sign-out-${which}`)?.focus()
}

/** After the list changes under the person's hands, the focus goes to its heading rather than to the page. */
async function afterChange() {
  confirming.value = null
  await load()
  await nextTick()
  listHeading.value?.focus()
}

async function signOut(session: SessionSummary) {
  busy.value = true
  actionError.value = null
  try {
    await api('DELETE', `/api/account/sessions/${encodeURIComponent(session.id)}`)
    done.value = t('settings.account.signedOutOne', { device: nameOf(session) })
  } catch (error) {
    const code = codeOf(error)
    // Already gone (it signed out, or another tab got there first): the list is read again and says so.
    if (code === 'session_not_found') done.value = t('settings.account.alreadyGone')
    else actionError.value = message(code)
  } finally {
    busy.value = false
  }
  await afterChange()
}

async function signOutOthers() {
  busy.value = true
  actionError.value = null
  try {
    const answer = await api<{ ended: number }>('POST', '/api/account/sessions/sign-out-others')
    done.value = t('settings.account.signedOutOthers', answer.ended)
  } catch (error) {
    actionError.value = message(codeOf(error))
  } finally {
    busy.value = false
  }
  await afterChange()
}

const row = 'flex flex-col gap-space-xs border-b border-border-subtle py-space-md last:border-b-0'
</script>

<template>
  <section
    aria-labelledby="account-settings"
    class="flex flex-col gap-space-lg"
    data-testid="account"
  >
    <h2 id="account-settings" class="text-headline-sm">{{ t('settings.account.title') }}</h2>

    <form class="flex flex-col gap-space-md" novalidate @submit.prevent="saveName">
      <TextField
        id="display-name"
        v-model="displayName"
        autocomplete="name"
        :label="t('fields.displayName')"
        :hint="t('settings.account.displayNameHint')"
        :error="nameError ?? undefined"
        required
      />
      <div class="flex flex-wrap items-center gap-space-md">
        <SubmitButton :busy="nameSaving">
          {{ nameSaving ? t('settings.saving') : t('settings.save') }}
        </SubmitButton>
        <p v-if="nameSaved" role="status" class="text-body-sm">{{ t('settings.saved') }}</p>
      </div>
    </form>

    <div class="flex flex-col gap-space-sm" data-testid="devices">
      <h3
        ref="listHeading"
        tabindex="-1"
        class="text-label-md focus-visible:outline-2 focus-visible:outline-primary"
      >
        {{ t('settings.account.devices') }}
      </h3>

      <div
        v-if="loadError"
        role="alert"
        class="flex flex-col items-start gap-space-sm text-body-sm"
      >
        <p>{{ t('settings.account.devicesFailed') }}</p>
        <AppButton @click="load">{{ t('settings.retry') }}</AppButton>
      </div>
      <p v-else-if="!sessions" role="status" class="text-body-sm">{{ t('settings.loading') }}</p>
      <template v-else>
        <p v-if="done" role="status" class="text-body-sm" data-testid="devices-done">{{ done }}</p>
        <p v-if="actionError" role="alert" class="text-body-sm text-error">{{ actionError }}</p>

        <ul class="flex flex-col" data-testid="device-list">
          <li v-for="session in sessions" :key="session.id" :class="row" data-testid="device">
            <p class="text-body-lg" :title="session.userAgent ?? undefined">
              {{ nameOf(session) }}
              <span
                v-if="session.current"
                class="ms-space-xs rounded bg-surface-container-high px-space-xs text-label-xs text-on-surface-variant"
                >{{ t('settings.account.thisDevice') }}</span
              >
            </p>
            <p class="text-body-sm text-on-surface-variant">
              {{ activity(session) }}
            </p>
            <template v-if="!session.current">
              <AppButton
                v-if="confirming !== session.id"
                :id="`sign-out-${session.id}`"
                class="self-start"
                :disabled="busy"
                @click="ask(session.id)"
              >
                {{ t('settings.account.signOut') }}
              </AppButton>
              <div
                v-else
                role="group"
                :aria-labelledby="`confirm-${session.id}`"
                class="flex flex-col gap-space-sm"
              >
                <p
                  :id="`confirm-${session.id}`"
                  tabindex="-1"
                  class="text-body-sm focus-visible:outline-2 focus-visible:outline-primary"
                >
                  {{ t('settings.account.confirmOne') }}
                </p>
                <div class="flex flex-wrap gap-space-sm">
                  <AppButton variant="primary" :disabled="busy" @click="signOut(session)">
                    {{ t('settings.account.signOut') }}
                  </AppButton>
                  <AppButton :disabled="busy" @click="cancel">{{ t('settings.cancel') }}</AppButton>
                </div>
              </div>
            </template>
          </li>
        </ul>
        <p v-if="more > 0" class="text-body-sm text-on-surface-variant">
          {{ t('settings.account.more', more) }}
        </p>

        <template v-if="others.length > 0 || more > 0">
          <AppButton
            v-if="confirming !== 'others'"
            id="sign-out-others"
            class="self-start"
            :disabled="busy"
            @click="ask('others')"
          >
            {{ t('settings.account.signOutOthers') }}
          </AppButton>
          <div
            v-else
            role="group"
            aria-labelledby="confirm-others"
            class="flex flex-col gap-space-sm rounded border border-border-subtle p-space-md"
          >
            <p
              id="confirm-others"
              tabindex="-1"
              class="text-body-sm focus-visible:outline-2 focus-visible:outline-primary"
            >
              {{ t('settings.account.confirmOthers') }}
            </p>
            <div class="flex flex-wrap gap-space-sm">
              <AppButton variant="primary" :disabled="busy" @click="signOutOthers">
                {{ t('settings.account.signOutOthers') }}
              </AppButton>
              <AppButton :disabled="busy" @click="cancel">{{ t('settings.cancel') }}</AppButton>
            </div>
          </div>
        </template>

        <p class="text-body-sm text-on-surface-variant">{{ t('settings.account.offlineNote') }}</p>
      </template>
    </div>
  </section>
</template>
