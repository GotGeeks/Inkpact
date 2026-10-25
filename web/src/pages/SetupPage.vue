<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ApiError } from '../api'
import AuthLayout from '../components/AuthLayout.vue'
import SubmitButton from '../components/SubmitButton.vue'
import TextField from '../components/TextField.vue'
import { useErrorMessage } from '../errors'
import { useAuthStore } from '../stores/auth'

const { t } = useI18n()
const message = useErrorMessage()
const auth = useAuthStore()
const router = useRouter()

const form = reactive({
  setupCode: '',
  displayName: '',
  email: '',
  password: '',
  workspaceName: t('setup.workspaceDefault'),
})
const error = ref('')
const fieldErrors = reactive<Record<string, string>>({})
const busy = ref(false)

async function submit() {
  if (busy.value) return
  busy.value = true
  error.value = ''
  for (const key of Object.keys(fieldErrors)) delete fieldErrors[key]
  try {
    await auth.setup({ ...form })
    await router.replace({ name: 'home' })
  } catch (caught) {
    const code = caught instanceof ApiError ? caught.code : 'unknown'
    const field = caught instanceof ApiError ? caught.field : undefined
    // An error that belongs to a field is shown under it; anything else, above the form.
    const wait = caught instanceof ApiError ? caught.retryAfterSeconds : undefined
    if (field && field in form) fieldErrors[field] = message(code, wait)
    else error.value = message(code, wait)
    form.password = ''
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <AuthLayout :title="t('setup.title')" :intro="t('setup.intro')">
    <form class="flex flex-col gap-space-lg" @submit.prevent="submit">
      <p
        v-if="error"
        role="alert"
        class="rounded border border-error bg-error-container p-space-md text-body-sm text-on-error-container"
      >
        {{ error }}
      </p>
      <TextField
        id="setup-code"
        v-model="form.setupCode"
        autocomplete="one-time-code"
        :label="t('setup.code')"
        :hint="t('setup.codeHint')"
        :error="fieldErrors.setupCode"
        required
      />
      <TextField
        id="display-name"
        v-model="form.displayName"
        autocomplete="name"
        :label="t('fields.displayName')"
        :error="fieldErrors.displayName"
        required
      />
      <TextField
        id="email"
        v-model="form.email"
        type="email"
        autocomplete="email"
        :label="t('fields.email')"
        :error="fieldErrors.email"
        required
      />
      <TextField
        id="password"
        v-model="form.password"
        type="password"
        autocomplete="new-password"
        :label="t('fields.password')"
        :hint="t('setup.passwordHint')"
        :error="fieldErrors.password"
        required
      />
      <TextField
        id="workspace-name"
        v-model="form.workspaceName"
        :label="t('setup.workspaceName')"
        :error="fieldErrors.workspaceName"
        required
      />
      <SubmitButton :busy="busy">{{ t('setup.submit') }}</SubmitButton>
    </form>
  </AuthLayout>
</template>
