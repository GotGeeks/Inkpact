<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import AppButton from '../components/AppButton.vue'
import { useVersionStore } from '../stores/version'

/**
 * Which Inkpact runs (run B.8), for everyone; for the administrator also a newer release when there is one, with its
 * notes and how to install it. The server asks GitHub; this screen only asks the server.
 */
const { t } = useI18n()
const versions = useVersionStore()

onMounted(() => void versions.load())

/** A build without a version stamped in it: a developer's. */
const DEVELOPMENT = '0.0.0-dev'
const versionText = computed(() => {
  const version = versions.info?.version
  if (!version) return ''
  return version === DEVELOPMENT
    ? t('settings.about.development')
    : t('settings.about.version', { version })
})

// Said after the link's words, for a screen reader; the template would drop a leading space.
const newTab = computed(() => ` ${t('settings.about.newTab')}`)

// Commands are not words to translate.
const composeCommand = 'docker compose pull && docker compose up -d'
</script>

<template>
  <section aria-labelledby="about-settings" class="flex flex-col gap-space-md" data-testid="about">
    <h2 id="about-settings" class="text-headline-sm">{{ t('settings.about.title') }}</h2>

    <div
      v-if="versions.error"
      role="alert"
      class="flex flex-col items-start gap-space-sm text-body-sm"
    >
      <p>{{ t('settings.about.loadFailed') }}</p>
      <AppButton @click="versions.load(true)">{{ t('settings.retry') }}</AppButton>
    </div>
    <p v-else-if="!versions.info && versions.loading" role="status" class="text-body-sm">
      {{ t('settings.loading') }}
    </p>
    <template v-else-if="versions.info">
      <p class="text-body-lg" data-testid="app-version">{{ versionText }}</p>

      <div
        v-if="versions.info.update"
        class="flex flex-col gap-space-sm rounded border border-primary p-space-md"
        data-testid="update"
      >
        <h3 class="text-label-md">
          {{ t('settings.about.updateTitle', { version: versions.info.update.version }) }}
        </h3>
        <p class="text-body-sm">
          <a
            :href="versions.info.update.url"
            target="_blank"
            rel="noopener noreferrer"
            class="inline-flex min-h-11 items-center text-primary underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary md:min-h-0"
            >{{ t('settings.about.releaseNotes', { version: versions.info.update.version })
            }}<span class="sr-only">{{ newTab }}</span></a
          >
        </p>
        <p class="text-body-sm">{{ t('settings.about.howUnraid') }}</p>
        <p class="text-body-sm">{{ t('settings.about.howCompose') }}</p>
        <code
          class="block overflow-x-auto rounded bg-surface-container-high p-space-sm text-body-sm"
          >{{ composeCommand }}</code
        >
      </div>
    </template>
  </section>
</template>
