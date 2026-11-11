<script setup lang="ts">
import type { ViewerClient } from './viewer-client'
import type { AnalysisFilter, AnalysisResult, SearchResult } from './recording-analysis'
import { ref, shallowRef, watch } from 'vue'
import Filters from './viewer-filters.vue'
const props = defineProps<{ client: ViewerClient; turn?: string }>()
defineEmits<{ record: [index: number] }>()
const filter = ref<AnalysisFilter>({}),
  text = ref(''),
  offset = ref(0),
  result = shallowRef<SearchResult>(),
  options = shallowRef<AnalysisResult['options']>(),
  loading = ref(false),
  error = ref('')
watch(
  () => props.client,
  async (client, _old, onCleanup) => {
    let current = true
    onCleanup(() => {
      current = false
    })
    try {
      const analysis = await client.remote.analyze()
      if (current) options.value = analysis.options
    } catch (value) {
      if (current) error.value = String(value)
    }
  },
  { immediate: true },
)
watch(
  [filter, text, () => props.turn],
  () => {
    offset.value = 0
  },
  { flush: 'sync' },
)
watch(
  () => [props.client, filter.value, text.value, offset.value, props.turn] as const,
  ([client, query, phrase, start], _old, onCleanup) => {
    let current = true
    loading.value = true
    error.value = ''
    const timer = setTimeout(async () => {
      try {
        const next = await client.remote.search({
          ...query,
          ...(props.turn ? { turn: props.turn } : {}),
          text: phrase,
          offset: start,
        })
        if (current) result.value = next
      } catch (value) {
        if (current) error.value = String(value)
      } finally {
        if (current) loading.value = false
      }
    }, 250)
    onCleanup(() => {
      current = false
      clearTimeout(timer)
    })
  },
  { immediate: true },
)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3 p-3 text-sm">
    <input
      v-model="text"
      type="search"
      aria-label="Search recorded content"
      placeholder="Search prompts, tool arguments, results, output…"
      class="w-full rounded border border-neutral-700 bg-neutral-900 px-3 py-2"
    />
    <details v-if="options" class="text-xs text-neutral-400">
      <summary class="mb-2 cursor-pointer">Advanced filters</summary>
      <Filters v-model="filter" :options="options" tools :scoped="!!turn" />
    </details>
    <p class="text-xs text-neutral-500">
      {{ turn ? 'Selected turn' : 'Whole session' }} · case-insensitive phrase search across records and
      referenced JSON/text attachments. Images and binary files are not searched. Run and turn filters include
      background origins.
    </p>
    <p v-if="error" class="text-red-400" role="alert">{{ error }}</p>
    <div class="flex flex-wrap items-center justify-between gap-2 text-xs text-neutral-400">
      <span role="status">{{
        loading ? 'Searching…' : `${result?.total.toLocaleString() ?? 0} matching records`
      }}</span>
      <div class="flex items-center gap-2">
        <button class="viewer-button py-1" :disabled="loading || offset === 0" @click="offset -= 50">
          Previous</button
        ><span>{{ result?.total ? offset + 1 : 0 }}–{{ Math.min(offset + 50, result?.total ?? 0) }}</span
        ><button
          class="viewer-button py-1"
          :disabled="loading || offset + 50 >= (result?.total ?? 0)"
          @click="offset += 50"
        >
          Next
        </button>
      </div>
    </div>
    <div class="min-h-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
      <button
        v-for="hit in result?.hits"
        :key="hit.index"
        class="block w-full space-y-1 border-t border-neutral-800 px-2 py-3 text-left hover:bg-neutral-900"
        @click="$emit('record', hit.index)"
      >
        <div class="flex flex-wrap gap-2">
          <span class="text-pink-300">{{ hit.name }}</span
          ><span class="text-xs text-neutral-500"
            >{{ (hit.time / 1000).toFixed(1) }}s · {{ hit.status ?? 'event' }}</span
          >
        </div>
        <div class="break-all text-xs text-neutral-500">{{ hit.track }}</div>
        <p class="whitespace-pre-wrap break-words text-xs text-neutral-300">{{ hit.snippet }}</p>
      </button>
    </div>
  </div>
</template>
