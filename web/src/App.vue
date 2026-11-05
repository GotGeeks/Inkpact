<script setup lang="ts">
import { RouterView } from 'vue-router'
import { useDocumentTitle } from './composables/useDocumentTitle'

useDocumentTitle()
</script>

<template>
  <RouterView />
</template>
