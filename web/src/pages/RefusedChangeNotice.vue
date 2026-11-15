<script setup lang="ts">
import { nextTick, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import AppButton from '../components/AppButton.vue'

/**
 * A change the server will not take (run B.10): the page reached its size limit (`full`), or one change is larger than
 * a message may be (`tooLarge`). The page stays readable, so the person can copy what they need; discarding the change,
 * after a question in place, opens the page again as the server has it.
 */
/** `offline`: the page cannot be opened again from the server now, so the change cannot be discarded yet. */
const props = defineProps<{ reason: 'full' | 'tooLarge'; offline: boolean }>()
const emit = defineEmits<{ discard: [] }>()

const { t } = useI18n()
const id = useId()
const asking = ref(false)

async function ask() {
  asking.value = true
  await nextTick()
  document.getElementById(`${id}-question`)?.focus()
}

async function keep() {
  asking.value = false
  await nextTick()
  document.getElementById(`${id}-discard`)?.focus()
}
</script>

<template>
  <div
    class="flex flex-col gap-space-sm rounded border border-error p-space-md text-body-sm"
    :data-testid="`refused-${props.reason}`"
  >
    <p role="alert" class="text-error">
      {{ props.reason === 'full' ? t('sync.full') : t('sync.tooLarge') }}
    </p>
    <p v-if="props.offline" :id="`${id}-offline`">{{ t('sync.discardOffline') }}</p>
    <AppButton
      v-if="!asking || props.offline"
      :id="`${id}-discard`"
      class="self-start"
      :disabled="props.offline"
      :aria-describedby="props.offline ? `${id}-offline` : undefined"
      @click="ask"
    >
      {{ t('sync.discard') }}
    </AppButton>
    <div v-else role="group" :aria-labelledby="`${id}-question`" class="flex flex-col gap-space-sm">
      <p
        :id="`${id}-question`"
        tabindex="-1"
        class="focus-visible:outline-2 focus-visible:outline-primary"
      >
        {{ t('sync.discardQuestion') }}
      </p>
      <div class="flex flex-wrap gap-space-sm">
        <AppButton variant="primary" @click="emit('discard')">
          {{ t('sync.discardConfirm') }}
        </AppButton>
        <AppButton @click="keep">{{ t('sync.keep') }}</AppButton>
      </div>
    </div>
  </div>
</template>
