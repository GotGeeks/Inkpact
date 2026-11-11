<script setup lang="ts">
import { RouterView } from 'vue-router'
import { useDocumentTitle } from './composables/useDocumentTitle'
import PwaNotice from './pwa/PwaNotice.vue'

useDocumentTitle()
</script>

<template>
  <RouterView />
  <PwaNotice />
</template>
