<script setup lang="ts">
import { shallowRef, watch } from 'vue'
import type { ViewerClient } from './viewer-client'
import Json from './viewer-json.vue'
const props = defineProps<{ client: ViewerClient; index: number }>()
const record = shallowRef<unknown>(),
  error = shallowRef('')
watch(
  () => [props.client, props.index] as const,
  async ([client, index], _old, onCleanup) => {
    let current = true
    onCleanup(() => {
      current = false
    })
    record.value = undefined
    error.value = ''
    try {
      const records = await client.remote.rawRecords([index])
      if (current) record.value = records[0]
    } catch (value) {
      if (current) error.value = String(value)
    }
  },
  { immediate: true },
)
</script>
<template>
  <div class="raw-record">
    <h3>Record #{{ index + 1 }}</h3>
    <p v-if="error" class="error">{{ error }}</p>
    <Json v-else-if="record" :data="record" />
    <p v-else>Reading record…</p>
  </div>
</template>
