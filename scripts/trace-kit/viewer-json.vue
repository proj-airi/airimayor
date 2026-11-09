<script setup lang="ts">
import VueJsonPretty from 'vue-json-pretty'
import type { JSONDataType } from 'vue-json-pretty/types/utils'
const props = defineProps<{ data: unknown }>()
function download() {
  const url = URL.createObjectURL(new Blob([JSON.stringify(props.data, null, 2)], { type: 'application/json' }))
  const link = document.createElement('a')
  link.href = url
  link.download = 'evidence.json'
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
</script>
<template>
  <div class="evidence-tree">
    <div class="tree-actions"><button class="button subtle" @click="download">Download JSON</button></div>
    <VueJsonPretty
      :data="(data ?? null) as JSONDataType"
      :deep="1"
      :collapsed-node-length="25"
      virtual
      :height="360"
      show-length
      theme="dark"
    />
  </div>
</template>
