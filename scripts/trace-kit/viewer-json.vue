<script setup lang="ts">
import type { JSONDataType } from 'vue-json-pretty/types/utils'
import { useElementSize } from '@vueuse/core'
import { useTemplateRef } from 'vue'
import VueJsonPretty from 'vue-json-pretty'

const props = defineProps<{ data: unknown }>()
const viewport = useTemplateRef<HTMLElement>('viewport')
const { height } = useElementSize(viewport)
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
  <div class="flex h-full min-h-0 flex-col overflow-hidden" aria-label="JSON viewer">
    <div class="flex shrink-0 justify-end px-3 py-1">
      <button class="viewer-button" @click="download">Download JSON</button>
    </div>
    <div ref="viewport" class="min-h-0 flex-1 overflow-hidden px-3">
      <VueJsonPretty
        v-if="height"
        class="font-mono text-sm [scrollbar-gutter:stable]"
        :data="(data ?? null) as JSONDataType"
        :deep="1"
        :collapsed-node-length="25"
        virtual
        :height="height"
        show-length
        theme="dark"
      />
    </div>
  </div>
</template>
