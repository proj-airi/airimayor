<script setup lang="ts">
import type { TraceItemRef } from '@proj-airi/trace-kit/views'
import type { ViewerClient } from './viewer-client'
import type { AnalysisFilter, AnalysisResult } from './recording-analysis'
import { computed, ref, shallowRef, watch } from 'vue'
import Filters from './viewer-filters.vue'

const props = defineProps<{ client: ViewerClient; turn?: string }>()
defineEmits<{ select: [item: TraceItemRef]; seek: [time: number] }>()
const filter = ref<AnalysisFilter>({}),
  offset = ref(0),
  result = shallowRef<AnalysisResult>(),
  error = ref(''),
  loading = ref(false)
watch(
  [filter, () => props.turn],
  () => {
    offset.value = 0
  },
  { flush: 'sync' },
)
watch(
  () => [props.client, filter.value, offset.value, props.turn] as const,
  async ([client, query, start], _old, onCleanup) => {
    let current = true
    onCleanup(() => {
      current = false
    })
    loading.value = true
    error.value = ''
    try {
      const next = await client.remote.analyze(
        { ...query, ...(props.turn ? { turn: props.turn } : {}) },
        start,
      )
      if (current) result.value = next
    } catch (value) {
      if (current) error.value = String(value)
    } finally {
      if (current) loading.value = false
    }
  },
  { immediate: true },
)
const number = (value?: number) =>
  value === undefined ? 'unavailable' : value.toLocaleString(undefined, { maximumFractionDigits: 1 })
const percent = (value?: number) => (value === undefined ? 'unavailable' : `${(value * 100).toFixed(1)}%`)
const ms = (value?: number) => (value === undefined ? '–' : `${number(value)} ms`)
const chart = computed(() => {
  const calls = result.value?.models ?? []
  const max = Math.max(1, ...calls.flatMap((call) => [call.input ?? 0, call.cache ?? 0]))
  return calls.map((call, index) => ({
    ...call,
    x: calls.length > 1 ? 20 + (index * 760) / (calls.length - 1) : 400,
    inputY: call.input === undefined ? undefined : 145 - (call.input / max) * 125,
    cacheY: call.cache === undefined ? undefined : 145 - (call.cache / max) * 125,
  }))
})
</script>

<template>
  <div class="space-y-5 p-3 text-sm">
    <p class="text-xs text-neutral-400">
      {{ turn ? 'Selected turn' : 'Whole session' }} · latency percentiles use successful calls; token
      totals include every reported usage, including failed or cancelled calls. Missing usage stays
      unavailable.
    </p>
    <details v-if="result" class="text-xs text-neutral-400">
      <summary class="mb-2 cursor-pointer">Advanced filters</summary>
      <Filters v-model="filter" :options="result.options" :scoped="!!turn" />
    </details>
    <p v-if="error" class="text-red-400" role="alert">{{ error }}</p>
    <p v-if="loading" class="text-neutral-400" role="status">Calculating…</p>
    <template v-if="result">
      <div class="flex flex-wrap gap-3">
        <div
          v-for="key in ['input', 'output', 'cache', 'reasoning'] as const"
          :key="key"
          class="rounded bg-neutral-900 px-3 py-2"
        >
          <div class="text-xs capitalize text-neutral-400">
            {{ key === 'cache' ? 'Cache-read' : key }} tokens
          </div>
          <div class="mt-1 tabular-nums">{{ number(result.usage[key].total) }}</div>
          <div class="text-xs text-neutral-500">
            {{ result.usage[key].calls }} / {{ result.calls }} calls reported
          </div>
        </div>
        <div class="rounded bg-neutral-900 px-3 py-2">
          <div class="text-xs text-neutral-400">Weighted cache reuse</div>
          <div class="mt-1">{{ percent(result.cacheRate) }}</div>
          <div class="text-xs text-neutral-500">{{ result.cacheCalls }} calls with input + cache usage</div>
        </div>
      </div>
      <p class="flex flex-wrap gap-3 text-xs text-neutral-400">
        <span>{{ result.calls }} model calls</span
        ><span v-for="(count, status) in result.outcomes" :key="status">{{ status }} {{ count }}</span>
      </p>
      <div class="overflow-x-auto">
        <table class="w-full text-left text-xs tabular-nums">
          <thead class="text-neutral-400">
            <tr>
              <th class="p-2">Successful-call latency</th>
              <th>p50</th>
              <th>p95</th>
              <th>Samples</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(values, key) in result.latency" :key="key" class="border-t border-neutral-800">
              <td class="p-2">
                {{
                  key === 'ttft'
                    ? 'First content (includes reasoning)'
                    : key === 'ttfat'
                      ? 'First answer / tool call'
                      : 'Total duration'
                }}
              </td>
              <td>{{ ms(values.p50) }}</td>
              <td>{{ ms(values.p95) }}</td>
              <td>{{ values.count }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <p class="text-xs text-neutral-500">
        First content ignores empty and usage-only updates. Timing uses recorded attributes or observed
        content timestamps; unstreamed content becomes observable at completion. Reasoning and cache counts
        are subsets of output and input respectively. AIRI leaves p95 unavailable below 100 valid samples.
      </p>
      <div v-if="result.agents.length" class="overflow-x-auto">
        <table class="w-full text-left text-xs tabular-nums">
          <thead class="text-neutral-400">
            <tr>
              <th class="p-2">Agent</th>
              <th>Calls</th>
              <th>Input</th>
              <th>Output</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="agent in result.agents" :key="agent.name" class="border-t border-neutral-800">
              <td class="p-2">{{ agent.name }}</td>
              <td>{{ agent.calls }}</td>
              <td>{{ number(agent.input) }}</td>
              <td>{{ number(agent.output) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <section class="space-y-2">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <h3 class="font-medium">Context and cache by request</h3>
          <span class="text-xs text-neutral-400"
            >Input <span class="text-pink-300">●</span> · cache-read <span class="text-emerald-300">●</span> ·
            current page</span
          >
        </div>
        <svg
          v-if="chart.length"
          viewBox="0 0 800 165"
          class="w-full rounded bg-neutral-900"
          role="img"
          aria-label="Input and cache-read tokens for the current page of model calls"
        >
          <line x1="20" y1="145" x2="780" y2="145" stroke="#525252" />
          <g
            v-for="call in chart"
            :key="call.key"
            tabindex="0"
            role="button"
            :aria-label="`${call.agent}: input ${number(call.input)}, cache ${number(call.cache)}`"
            class="cursor-pointer"
            @click="$emit('select', { kind: 'span', key: call.key })"
            @keydown.enter="$emit('select', { kind: 'span', key: call.key })"
          >
            <title>
              {{ call.agent }} · {{ call.status }} · input {{ number(call.input) }} · cache
              {{ number(call.cache) }} ·
              {{ percent(call.cacheRate) }}
            </title>
            <line
              v-if="call.inputY !== undefined"
              :x1="call.x"
              y1="145"
              :x2="call.x"
              :y2="call.inputY"
              stroke="#f9a8d4"
              stroke-opacity="0.5"
            />
            <circle v-if="call.inputY !== undefined" :cx="call.x" :cy="call.inputY" r="3" fill="#f9a8d4" />
            <circle v-if="call.cacheY !== undefined" :cx="call.x" :cy="call.cacheY" r="2.5" fill="#6ee7b7" />
          </g>
        </svg>
        <div class="overflow-x-auto">
          <table class="w-full whitespace-nowrap text-left text-xs tabular-nums">
            <thead class="text-neutral-400">
              <tr>
                <th class="p-2">Call</th>
                <th class="px-2">Recorded outcome</th>
                <th class="px-2">Input</th>
                <th class="px-2">Δ input¹</th>
                <th class="px-2">Cache %</th>
                <th class="px-2">Output</th>
                <th class="px-2">Reasoning</th>
                <th class="px-2">Duration</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(call, index) in result.models" :key="call.key" class="border-t border-neutral-800">
                <td class="p-2">
                  <button
                    class="text-pink-300 hover:underline"
                    @click="$emit('select', { kind: 'span', key: call.key })"
                  >
                    {{ offset + index + 1 }} · {{ call.agent }}
                  </button>
                </td>
                <td class="px-2">{{ call.status }}</td>
                <td class="px-2">{{ number(call.input) }}</td>
                <td class="px-2">{{ number(call.inputGrowth) }}</td>
                <td class="px-2">{{ percent(call.cacheRate) }}</td>
                <td class="px-2">{{ number(call.output) }}</td>
                <td class="px-2">{{ number(call.reasoning) }}</td>
                <td class="px-2">{{ ms(call.duration) }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <div class="flex items-center justify-between text-xs">
          <span class="text-neutral-500"
            >¹ Change from the preceding reported request in the same agent session.</span
          >
          <div class="flex items-center gap-2">
            <button class="viewer-button py-1" :disabled="offset === 0" @click="offset -= 100">
              Previous</button
            ><span
              >{{ result.modelTotal ? offset + 1 : 0 }}–{{ Math.min(offset + 100, result.modelTotal) }} /
              {{ result.modelTotal }}</span
            ><button
              class="viewer-button py-1"
              :disabled="offset + 100 >= result.modelTotal"
              @click="offset += 100"
            >
              Next
            </button>
          </div>
        </div>
      </section>
      <section class="space-y-2">
        <h3 class="font-medium">Compaction checkpoints</h3>
        <p class="text-xs text-neutral-400">
          {{ result.compactionTotal }} recorded operations · showing the last 100. Counts here are history
          token estimates; request usage above is provider-reported.
        </p>
        <div class="max-h-72 overflow-auto">
          <table class="w-full whitespace-nowrap text-left text-xs tabular-nums">
            <thead class="text-neutral-400">
              <tr>
                <th class="p-2">Time / agent</th>
                <th class="px-2">Outcome</th>
                <th class="px-2">Before → after</th>
                <th class="px-2">Input budget</th>
                <th class="px-2">Trigger / target</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="call in result.compactions" :key="call.index" class="border-t border-neutral-800">
                <td class="p-2">
                  <button class="text-pink-300 hover:underline" @click="$emit('seek', call.time)">
                    {{ (call.time / 1000).toFixed(1) }}s · {{ call.agent }}
                  </button>
                </td>
                <td class="px-2">{{ call.status }} · {{ call.changed ? 'changed' : 'unchanged' }}</td>
                <td class="px-2">{{ number(call.before) }} → {{ number(call.after) }}</td>
                <td class="px-2">{{ number(call.budget) }}</td>
                <td class="px-2">{{ number(call.trigger) }} / {{ number(call.target) }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
      <section class="space-y-2">
        <h3 class="font-medium">Tool execution</h3>
        <p class="text-xs text-neutral-400">
          Duration p50/p95 uses successful calls. Queue p50/p95 uses all reported samples. Write verdicts
          describe ledger evidence, independently of the tool outcome.
        </p>
        <div class="overflow-x-auto">
          <table class="w-full whitespace-nowrap text-left text-xs tabular-nums">
            <thead class="text-neutral-400">
              <tr>
                <th class="p-2">Tool</th>
                <th class="px-2">Calls</th>
                <th class="px-2">OK / error / cancelled / incomplete</th>
                <th class="px-2">Duration p50 / p95 (n)</th>
                <th class="px-2">Queue p50 / p95 (n)</th>
                <th class="px-2">Confirmed / diverged / undetermined / invalid</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="tool in result.tools" :key="tool.name" class="border-t border-neutral-800">
                <td class="p-2">{{ tool.name }}</td>
                <td class="px-2">{{ tool.calls }}</td>
                <td class="px-2">
                  {{ tool.ok }} / {{ tool.error }} / {{ tool.cancelled }} / {{ tool.incomplete }}
                </td>
                <td class="px-2">
                  {{ ms(tool.duration.p50) }} / {{ ms(tool.duration.p95) }} ({{ tool.duration.count }})
                </td>
                <td class="px-2">
                  {{ ms(tool.queued.p50) }} / {{ ms(tool.queued.p95) }} ({{ tool.queued.count }})
                </td>
                <td class="px-2">
                  {{ tool.confirmed }} / {{ tool.diverged }} / {{ tool.undetermined }} / {{ tool.invalid }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
      <details v-if="result.gaps.length" class="text-xs text-neutral-400">
        <summary class="cursor-pointer">Declared recording gaps</summary>
        <p v-for="gap in result.gaps" :key="gap" class="mt-2">{{ gap }}</p>
      </details>
    </template>
  </div>
</template>
