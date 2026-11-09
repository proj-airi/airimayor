<script setup lang="ts">
import type { AnalysisFilter, AnalysisResult } from './recording-analysis'
import { computed } from 'vue'
const props = defineProps<{ options: AnalysisResult['options']; tools?: boolean }>()
const filter = defineModel<AnalysisFilter>({ required: true })
const fields = computed(() => {
  const values: { key: keyof AnalysisFilter; title: string; values: string[] }[] = [
    { key: 'agent', title: 'Agent', values: props.options.agents },
    { key: 'run', title: 'Run / origin', values: props.options.runs },
    { key: 'turn', title: 'Turn / origin', values: props.options.turns },
    { key: 'status', title: 'Outcome', values: ['ok', 'error', 'cancelled', 'incomplete'] },
  ]
  if (props.tools) values.push({ key: 'tool', title: 'Tool', values: props.options.tools })
  return values
})
</script>

<template>
  <div class="flex flex-wrap items-end gap-2 text-xs">
    <label v-for="{ key, title, values } in fields" :key="key" class="flex min-w-0 flex-col gap-1 text-neutral-400">
      {{ title }}
      <select
        :value="filter[key as keyof AnalysisFilter] ?? ''"
        :aria-label="title"
        class="max-w-44 rounded border border-neutral-700 bg-neutral-900 px-2 py-1.5 text-neutral-200"
        @change="filter = { ...filter, [key]: ($event.target as HTMLSelectElement).value }"
      >
        <option value="">All</option>
        <option v-for="value in values" :key="value" :value="value">{{ value }}</option>
      </select>
    </label>
    <button class="viewer-button py-1.5 text-xs" @click="filter = {}">Reset filters</button>
  </div>
</template>
