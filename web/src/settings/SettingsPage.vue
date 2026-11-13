<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, useTemplateRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, ApiError } from '../api'
import AppButton from '../components/AppButton.vue'
import SubmitButton from '../components/SubmitButton.vue'
import { useErrorMessage } from '../errors'
import { formatFileSize } from '../fileSize'
import { useTreeStore } from '../stores/tree'
import AboutSection from './AboutSection.vue'
import {
  MAXIMUM_CAP_MIB,
  policyProblem,
  removesVersions,
  type HistoryPolicy,
  type HistoryUsage,
  type PolicyProblem,
} from './historyPolicy'

/**
 * The settings of the workspace (run B.5b, review finding 5.6): its history, which every member can read and only the
 * owner can change, and which Inkpact runs (run B.8). Run B.9 adds the person's own account here.
 */
const { t, locale } = useI18n()
const message = useErrorMessage()
const tree = useTreeStore()

const isOwner = computed(() => tree.workspace?.role === 'owner')
const workspaceId = computed(() => tree.workspace?.id)

/** The policy as the server has it, and as the form shows it (text, as typed). */
const saved = ref<HistoryPolicy | null>(null)
const usage = ref<HistoryUsage | null>(null)
const form = reactive({
  enabled: true,
  granularHours: '',
  dailyDays: '',
  expiryDays: '',
  capped: false,
  capMiB: '',
})
const loadError = ref<string | null>(null)
const saveError = ref<string | null>(null)
const problem = ref<PolicyProblem | null>(null)
const saving = ref(false)
const done = ref(false)
/** The warning before a save that can remove versions is showing. */
const warning = ref(false)
const warningHeading = useTemplateRef<HTMLElement>('warningHeading')
const formElement = useTemplateRef<HTMLFormElement>('formElement')

/** When the warning goes, the focus goes back to Save, which takes its place (once a save is over: busy, it is disabled). */
async function backToSave() {
  await nextTick()
  formElement.value?.querySelector<HTMLButtonElement>('button[type="submit"]')?.focus()
}

function cancelWarning() {
  warning.value = false
  void backToSave()
}

function show(policy: HistoryPolicy) {
  saved.value = policy
  form.enabled = policy.enabled
  form.granularHours = String(policy.granularHours)
  form.dailyDays = String(policy.dailyDays)
  form.expiryDays = String(policy.expiryDays)
  form.capped = policy.capMiB !== null
  form.capMiB = policy.capMiB === null ? '' : String(policy.capMiB)
}

/** Reads the policy, and first the workspace when it is not known yet (this screen opened straight from its address). */
async function load() {
  loadError.value = null
  try {
    if (!tree.loaded) await tree.init()
    const id = workspaceId.value
    // No workspace to read: said like a failed read, with a way to try again.
    if (!id) throw new ApiError(0, 'unknown')
    const [policy, used] = await Promise.all([
      api<HistoryPolicy>('GET', `/api/workspaces/${id}/history-policy`),
      api<HistoryUsage>('GET', `/api/workspaces/${id}/history-usage`),
    ])
    show(policy)
    usage.value = used
  } catch (error) {
    loadError.value = error instanceof ApiError ? error.code : 'unknown'
  }
}

onMounted(load)

const asNumber = (text: string) => (/^\d+$/.test(text.trim()) ? Number(text.trim()) : Number.NaN)
const asked = computed<HistoryPolicy>(() => ({
  enabled: form.enabled,
  granularHours: asNumber(form.granularHours),
  dailyDays: asNumber(form.dailyDays),
  expiryDays: asNumber(form.expiryDays),
  capMiB: form.capped ? asNumber(form.capMiB) : null,
}))

// The keys written out, so the lint rule that finds unused translations can see them.
function problemText(which: PolicyProblem): string {
  switch (which) {
    case 'granular':
      return t('settings.history.invalid.granular')
    case 'daily':
      return t('settings.history.invalid.daily')
    case 'dailyBeforeGranular':
      return t('settings.history.invalid.dailyBeforeGranular')
    case 'expiry':
      return t('settings.history.invalid.expiry')
    case 'expiryBeforeDaily':
      return t('settings.history.invalid.expiryBeforeDaily')
    case 'cap':
      return t('settings.history.invalid.cap', {
        max: new Intl.NumberFormat(locale.value).format(MAXIMUM_CAP_MIB),
      })
  }
}

async function save(confirmed = false) {
  done.value = false
  saveError.value = null
  problem.value = policyProblem(asked.value)
  if (problem.value || !saved.value || !workspaceId.value) return
  if (!confirmed && removesVersions(saved.value, asked.value)) {
    warning.value = true
    await nextTick()
    warningHeading.value?.focus()
    return
  }

  warning.value = false
  saving.value = true
  try {
    show(
      await api<HistoryPolicy>(
        'PUT',
        `/api/workspaces/${workspaceId.value}/history-policy`,
        asked.value,
      ),
    )
    usage.value = await api<HistoryUsage>(
      'GET',
      `/api/workspaces/${workspaceId.value}/history-usage`,
    ).catch(() => usage.value)
    done.value = true
  } catch (error) {
    saveError.value = error instanceof ApiError ? error.code : 'unknown'
  } finally {
    saving.value = false
  }
  if (confirmed) await backToSave()
}

function saveErrorText(code: string): string {
  switch (code) {
    case 'history_cap_below_protected':
      return t('settings.history.capBelowProtected')
    case 'history_policy_invalid':
      return t('settings.history.invalid.policy')
    default:
      return message(code)
  }
}

const size = (bytes: number) => formatFileSize(bytes, locale.value)
const usedText = computed(() => {
  if (!usage.value || !saved.value) return ''
  const used = size(usage.value.usedBytes)
  return saved.value.capMiB === null
    ? t('settings.history.used', { used })
    : t('settings.history.usedOfCap', { used, cap: size(saved.value.capMiB * 1024 * 1024) })
})

const field =
  'min-h-11 w-28 rounded border border-outline bg-surface-canvas px-space-md text-body-lg text-on-surface focus:border-primary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary disabled:opacity-60 md:min-h-9'
</script>

<template>
  <section class="mx-auto flex max-w-2xl flex-col gap-space-lg p-margin md:p-margin-desktop">
    <h1 class="text-headline-xl-mobile md:text-headline-xl">{{ t('settings.title') }}</h1>

    <section
      aria-labelledby="history-settings"
      class="flex flex-col gap-space-md"
      data-testid="history-settings"
    >
      <h2 id="history-settings" class="text-headline-sm">{{ t('settings.history.title') }}</h2>
      <p class="text-body-sm text-on-surface-variant">{{ t('settings.history.description') }}</p>

      <div
        v-if="loadError"
        role="alert"
        class="flex flex-col items-start gap-space-sm text-body-sm"
      >
        <p>{{ t('settings.history.loadFailed') }}</p>
        <AppButton @click="load">{{ t('settings.retry') }}</AppButton>
      </div>
      <p v-else-if="!saved" role="status" class="text-body-sm">{{ t('settings.loading') }}</p>

      <form
        v-else
        ref="formElement"
        class="flex flex-col gap-space-md"
        novalidate
        @submit.prevent="save()"
      >
        <p v-if="!isOwner" class="text-body-sm text-on-surface-variant" data-testid="owner-only">
          {{ t('settings.history.ownerOnly') }}
        </p>

        <label class="flex min-h-11 items-center gap-space-sm text-body-lg md:min-h-9">
          <input
            v-model="form.enabled"
            type="checkbox"
            :disabled="!isOwner"
            class="size-11 accent-primary md:size-5"
          />
          {{ t('settings.history.enabled') }}
        </label>
        <p v-if="!form.enabled" class="text-body-sm text-on-surface-variant">
          {{ t('settings.history.offNote') }}
        </p>

        <fieldset class="flex flex-col gap-space-md" :disabled="!isOwner">
          <legend class="mb-space-sm text-label-md">{{ t('settings.history.windows') }}</legend>
          <div class="flex flex-col gap-space-xs">
            <label for="granular-hours" class="text-body-sm">{{
              t('settings.history.granular')
            }}</label>
            <input
              id="granular-hours"
              v-model="form.granularHours"
              inputmode="numeric"
              :class="field"
            />
          </div>
          <div class="flex flex-col gap-space-xs">
            <label for="daily-days" class="text-body-sm">{{ t('settings.history.daily') }}</label>
            <input id="daily-days" v-model="form.dailyDays" inputmode="numeric" :class="field" />
          </div>
          <div class="flex flex-col gap-space-xs">
            <label for="expiry-days" class="text-body-sm">{{ t('settings.history.expiry') }}</label>
            <input id="expiry-days" v-model="form.expiryDays" inputmode="numeric" :class="field" />
          </div>
        </fieldset>

        <fieldset class="flex flex-col gap-space-sm" :disabled="!isOwner">
          <legend class="mb-space-sm text-label-md">{{ t('settings.history.capTitle') }}</legend>
          <label class="flex min-h-11 items-center gap-space-sm text-body-lg md:min-h-9">
            <input v-model="form.capped" type="checkbox" class="size-11 accent-primary md:size-5" />
            {{ t('settings.history.capped') }}
          </label>
          <div v-if="form.capped" class="flex flex-col gap-space-xs">
            <label for="cap-mib" class="text-body-sm">{{ t('settings.history.capMiB') }}</label>
            <input id="cap-mib" v-model="form.capMiB" inputmode="numeric" :class="field" />
          </div>
          <p class="text-body-sm text-on-surface-variant">{{ t('settings.history.capCounts') }}</p>
        </fieldset>

        <p v-if="usedText" class="text-body-sm" data-testid="history-usage">{{ usedText }}</p>

        <p v-if="problem" role="alert" class="text-body-sm text-error" data-testid="policy-problem">
          {{ problemText(problem) }}
        </p>

        <div
          v-if="warning"
          role="group"
          aria-labelledby="policy-warning-heading"
          class="flex flex-col gap-space-sm rounded border border-error p-space-md"
          data-testid="policy-warning"
        >
          <h3
            id="policy-warning-heading"
            ref="warningHeading"
            tabindex="-1"
            class="text-label-md focus-visible:outline-2 focus-visible:outline-primary"
          >
            {{ t('settings.history.warningTitle') }}
          </h3>
          <p class="text-body-sm">{{ t('settings.history.warning') }}</p>
          <div class="flex flex-wrap gap-space-sm">
            <AppButton variant="primary" :disabled="saving" @click="save(true)">
              {{ t('settings.history.saveAnyway') }}
            </AppButton>
            <AppButton :disabled="saving" @click="cancelWarning">{{
              t('settings.cancel')
            }}</AppButton>
          </div>
        </div>

        <div v-if="isOwner && !warning" class="flex flex-wrap items-center gap-space-md">
          <SubmitButton :busy="saving">
            {{ saving ? t('settings.saving') : t('settings.save') }}
          </SubmitButton>
          <p v-if="done" role="status" class="text-body-sm">{{ t('settings.saved') }}</p>
        </div>
        <p v-if="saveError" role="alert" class="text-body-sm text-error" data-testid="save-error">
          {{ saveErrorText(saveError) }}
        </p>
      </form>
    </section>

    <AboutSection />
  </section>
</template>
