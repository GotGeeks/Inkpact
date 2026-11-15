<script setup lang="ts">
import { nextTick, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, ApiError } from '../api'
import SubmitButton from '../components/SubmitButton.vue'
import TextField from '../components/TextField.vue'
import { useErrorMessage } from '../errors'
import { useAuthStore } from '../stores/auth'

/**
 * A new password, with the current one asked (run B.9). The server signs every other device out with it; this one stays
 * signed in. `changed` tells the account section, which reads the device list again.
 */
const emit = defineEmits<{ changed: [] }>()

const { t } = useI18n()
const message = useErrorMessage()
const auth = useAuthStore()

const form = reactive({ currentPassword: '', newPassword: '' })
type Field = keyof typeof form
const fieldIds: Record<Field, string> = {
  currentPassword: 'current-password',
  newPassword: 'new-password',
}
const fieldErrors = reactive<Partial<Record<Field, string>>>({})
const error = ref<string | null>(null)
const busy = ref(false)
const changed = ref(false)

const isField = (field: string | undefined): field is Field => !!field && field in form

async function submit() {
  if (busy.value) return
  busy.value = true
  error.value = null
  changed.value = false
  delete fieldErrors.currentPassword
  delete fieldErrors.newPassword
  try {
    await api('POST', '/api/account/password', { ...form })
    form.currentPassword = ''
    form.newPassword = ''
    changed.value = true
    emit('changed')
  } catch (caught) {
    const code = caught instanceof ApiError ? caught.code : 'unknown'
    const field = caught instanceof ApiError ? caught.field : undefined
    const wait = caught instanceof ApiError ? caught.retryAfterSeconds : undefined
    // An error that belongs to a field is shown under it, and the focus goes there; anything else, above the form.
    if (isField(field)) {
      fieldErrors[field] = message(code, wait)
      await nextTick()
      document.getElementById(fieldIds[field])?.focus()
    } else {
      error.value = message(code, wait)
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <form
    class="flex flex-col gap-space-md"
    aria-labelledby="password-settings"
    novalidate
    data-testid="password-form"
    @submit.prevent="submit"
  >
    <h3 id="password-settings" class="text-label-md">{{ t('settings.account.password') }}</h3>
    <p v-if="error" role="alert" class="text-body-sm text-error">{{ error }}</p>
    <!-- For password managers: the account the new password belongs to, so they update its saved entry. -->
    <input
      type="text"
      class="hidden"
      autocomplete="username"
      :value="auth.user?.email ?? ''"
      readonly
      tabindex="-1"
      aria-hidden="true"
    />
    <TextField
      :id="fieldIds.currentPassword"
      v-model="form.currentPassword"
      type="password"
      autocomplete="current-password"
      :label="t('settings.account.currentPassword')"
      :error="fieldErrors.currentPassword"
      required
    />
    <TextField
      :id="fieldIds.newPassword"
      v-model="form.newPassword"
      type="password"
      autocomplete="new-password"
      :label="t('settings.account.newPassword')"
      :hint="t('setup.passwordHint')"
      :error="fieldErrors.newPassword"
      required
    />
    <div class="flex flex-wrap items-center gap-space-md">
      <SubmitButton :busy="busy">
        {{ busy ? t('settings.saving') : t('settings.account.changePassword') }}
      </SubmitButton>
      <p v-if="changed" role="status" class="text-body-sm">
        {{ t('settings.account.passwordChanged') }}
      </p>
    </div>
  </form>
</template>
