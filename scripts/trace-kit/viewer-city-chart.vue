<script setup lang="ts">
import type { CityMetric, CityPoint, CityReplayPoint, CityOverview } from './city-view'
import { computed, ref, shallowRef } from 'vue'
import { TimelineRoot, TimelineAxis, TimelineScrollArea } from '@proj-airi/ui-timeline/headless'
import Plot from './viewer-city-plot.vue'
import { cityDate, cityMetricLabel, cityValue } from './city-chart'

const props = defineProps<{
  metric: CityMetric
  current?: CityMetric
  points: CityReplayPoint[]
  total: number
  cursor: number
  start: number
  end: number
  index: number
  limit?: number
  markers: CityOverview['markers']
}>()
const emit = defineEmits<{
  time: [time: number]
  record: [index: number]
  range: [limit: number | undefined]
}>()
const hovered = shallowRef<CityReplayPoint>()
const current = computed<CityPoint | undefined>(() => hovered.value ?? props.current?.latest)
const preview = computed(() => current.value !== undefined && current.value.index > props.index)
const showActions = ref(false),
  windowSize = ref(props.limit === undefined ? 'all' : String(props.limit))
const root = ref<{ fit: () => void }>()
</script>

<template>
  <section aria-label="City trend" class="min-w-0">
    <div class="flex min-h-12 flex-wrap items-start justify-between gap-2 px-1 pb-2">
      <div>
        <h3 class="text-sm font-medium">{{ cityMetricLabel(metric) }}</h3>
        <p class="mt-0.5 text-xs text-neutral-500">
          {{ current ? cityDate(current) : 'No reading reached at replay position' }}
          <span v-if="preview"> · preview ahead of replay</span>
        </p>
      </div>
      <span data-city-active-value class="text-lg tabular-nums">{{ cityValue(current?.value, metric.unit) }}</span>
    </div>
    <div class="mb-1 flex items-center justify-between text-xs text-neutral-500">
      <span>Elapsed recording time</span>
      <button class="viewer-icon-button" aria-label="Fit city timeline" @click="root?.fit()">
        <span class="i-mingcute:arrows-fullscreen-line size-4" aria-hidden="true" />
      </button>
    </div>
    <TimelineRoot
      ref="root"
      :start="start"
      :end="end"
      :cursor="cursor"
      :label-width="60"
      :aria-label="`${cityMetricLabel(metric)} chart controls`"
      class="h-44 w-full overflow-hidden rounded-md border border-neutral-800 bg-neutral-900/40 outline-none focus-visible:border-primary-300 sm:h-56 xl:h-64"
      @update:cursor="$event !== undefined && emit('time', $event)"
    >
      <TimelineScrollArea class="flex h-full min-h-0 flex-col overflow-hidden">
        <TimelineAxis
          v-slot="{ ticks, position }"
          aria-label="City replay cursor"
          class="relative h-6 shrink-0 overflow-hidden border-b border-neutral-800 bg-neutral-900 cursor-col-resize touch-none"
        >
          <div
            v-for="tick in ticks"
            :key="tick.time"
            :style="position(tick.time)"
            class="absolute top-0 h-full whitespace-nowrap border-l border-neutral-800 pl-1 text-[10px] leading-6 tabular-nums text-neutral-500"
          >
            {{ tick.label }}
          </div>
          <div
            class="absolute bottom-0 -ml-1.25 size-0 border-x-5 border-t-6 border-x-transparent border-t-sky-400"
            :style="position(cursor)"
          />
          <div class="absolute inset-y-0 left-0 w-15 bg-neutral-900" />
        </TimelineAxis>
        <Plot
          :metric="metric"
          :points="points"
          :total="total"
          :index="index"
          :markers="markers"
          :show-actions="showActions"
          @hover="hovered = $event"
          @record="emit('record', $event)"
        />
      </TimelineScrollArea>
    </TimelineRoot>
    <div class="flex flex-wrap items-center justify-between gap-2 pt-2 text-xs text-neutral-500">
      <div class="flex flex-wrap items-center gap-2">
        <span>{{ total }} recorded {{ total === 1 ? 'observation' : 'observations' }}</span>
        <select
          v-if="total > 1"
          v-model="windowSize"
          aria-label="Trend range"
          class="rounded bg-neutral-900 px-2 py-1 text-neutral-400"
          @change="emit('range', windowSize === 'all' ? undefined : Number(windowSize))"
        >
          <option value="all">Full recording</option>
          <option value="50">Last 50 observations</option>
          <option value="200">Last 200 observations</option>
        </select>
        <button
          class="flex items-center gap-1 rounded px-1.5 py-1 hover:bg-neutral-800"
          :class="showActions ? 'text-amber-300' : ''"
          :aria-pressed="showActions"
          @click="showActions = !showActions"
        >
          <span class="i-mingcute:building-2-line size-3.5" aria-hidden="true" />Actions
        </button>
      </div>
      <button
        class="flex items-center gap-1 rounded px-1.5 py-1 text-primary-300 hover:bg-neutral-800 disabled:text-neutral-600"
        aria-label="Open trend evidence"
        :disabled="!current"
        @click="current && emit('record', current.index)"
      >
        <span class="i-mingcute:file-search-line size-3.5" aria-hidden="true" />Source
      </button>
    </div>
  </section>
</template>
