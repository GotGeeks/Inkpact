<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ApiError } from '../api'
import AuthLayout from '../components/AuthLayout.vue'
import SubmitButton from '../components/SubmitButton.vue'
import TextField from '../components/TextField.vue'
import { useErrorMessage } from '../errors'
import { safeRedirect } from '../router'
import { useAuthStore } from '../stores/auth'

const { t } = useI18n()
const message = useErrorMessage()
const auth = useAuthStore()
const router = useRouter()

const email = ref('')
const password = ref('')
const error = ref('')
const busy = ref(false)

async function submit() {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    await auth.login(email.value, password.value)
    await router.replace(safeRedirect(router.currentRoute.value.query.redirect) ?? { name: 'home' })
  } catch (caught) {
    error.value = message(caught instanceof ApiError ? caught.code : 'unknown')
    password.value = ''
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <AuthLayout :title="t('login.title')" :intro="t('app.tagline')">
    <form class="flex flex-col gap-space-lg" @submit.prevent="submit">
      <p
        v-if="error"
        role="alert"
        class="rounded border border-error bg-error-container p-space-md text-body-sm text-on-error-container"
      >
        {{ error }}
      </p>
      <TextField
        id="email"
        v-model="email"
        type="email"
        autocomplete="email"
        :label="t('fields.email')"
        required
      />
      <TextField
        id="password"
        v-model="password"
        type="password"
        autocomplete="current-password"
        :label="t('fields.password')"
        required
      />
      <SubmitButton :busy="busy">{{ t('login.submit') }}</SubmitButton>
    </form>
  </AuthLayout>
</template>
