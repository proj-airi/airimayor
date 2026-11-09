<script setup lang="ts">
import type { CityEvent, CityMetric, CityPoint, CityStamp } from './city-view'
import { computed, ref, useTemplateRef, watch } from 'vue'
import { useElementSize } from '@vueuse/core'
import { cityChart, cityDate, cityMetricLabel, cityNumber, cityValue } from './city-chart'
const props = defineProps<{
  metric: CityMetric
  points: CityPoint[]
  total: number
  clocks: CityStamp[]
  frame?: number
  markers: Pick<CityEvent, 'index' | 'label' | 'status' | 'frame'>[]
}>()
const emit = defineEmits<{
  frame: [frame: number]
  record: [index: number]
  range: [limit: number | undefined]
}>()
const root = useTemplateRef<HTMLElement>('root'),
  { width, height } = useElementSize(root)
const active = ref<number>(),
  showActions = ref(false),
  windowSize = ref('all')
const chart = computed(() =>
  cityChart(props.points, props.clocks, Math.max(300, width.value), Math.max(150, height.value)),
)
const current = computed(() => (active.value === undefined ? props.metric.latest : chart.value.points[active.value]))
const cursor = computed(() => chart.value.points.find((point) => point.frame === current.value?.frame))
watch(
  () => props.metric.key,
  () => {
    active.value = undefined
  },
)
watch(
  () => props.frame,
  () => {
    active.value = undefined
  },
)
watch(
  () => props.points,
  () => {
    active.value = undefined
  },
)
const change = computed(() => {
  const first = props.points[0]
  return props.points.length > 1 && current.value && current.value.frame >= first.frame
    ? current.value.value - first.value
    : undefined
})
const actions = computed(() => {
  const groups = new Map<number, { x: number; count: number; index: number; label: string }>()
  for (const event of props.markers) {
    if (event.frame === undefined || event.frame < chart.value.start || event.frame > chart.value.end) continue
    const x = chart.value.x(event.frame),
      bucket = Math.floor(x / 24),
      group = groups.get(bucket)
    if (group) group.count++
    else groups.set(bucket, { x, count: 1, index: event.index, label: event.label })
  }
  return [...groups.values()]
})
function pointAt(event: MouseEvent) {
  const bounds = root.value!.getBoundingClientRect(),
    x = event.clientX - bounds.left
  let nearest = 0
  chart.value.points.forEach((point, index) => {
    if (Math.abs(point.x - x) < Math.abs(chart.value.points[nearest].x - x)) nearest = index
  })
  if (chart.value.points.length) active.value = nearest
}
function selectPoint(event: MouseEvent) {
  pointAt(event)
  if (current.value) emit('frame', current.value.frame)
}
function step(direction: number) {
  const start =
    active.value ??
    Math.max(
      0,
      chart.value.points.findIndex((point) => point.frame === current.value?.frame),
    )
  active.value = Math.max(0, Math.min(chart.value.points.length - 1, start + direction))
}
</script>

<template>
  <section aria-label="City trend" class="min-w-0">
    <div class="flex min-h-12 flex-wrap items-start justify-between gap-2 px-1 pb-2">
      <div>
        <h3 class="text-sm font-medium">{{ cityMetricLabel(metric) }}</h3>
        <p class="mt-0.5 text-xs text-neutral-500">
          {{ cityDate(current) }}<span v-if="!current"> · no reading at this city time</span>
          <span v-else-if="!cursor"> · outside the shown range</span>
        </p>
      </div>
      <div class="text-right">
        <span data-city-active-value class="text-lg tabular-nums">{{ cityValue(current?.value, metric.unit) }}</span
        ><span v-if="change !== undefined" class="ml-2 text-xs tabular-nums text-neutral-400"
          >{{ change >= 0 ? '+' : '' }}{{ cityNumber(change, true) }} over shown period</span
        >
      </div>
    </div>
    <div
      ref="root"
      tabindex="0"
      role="group"
      :aria-label="`${cityMetricLabel(metric)} chart controls`"
      class="relative h-44 w-full rounded-md border border-neutral-800 bg-neutral-900/40 outline-none focus-visible:border-pink-300 sm:h-56 xl:h-64"
      @keydown.left.prevent="step(-1)"
      @keydown.right.prevent="step(1)"
      @keydown.enter.prevent="current && emit('frame', current.frame)"
      @pointermove="pointAt"
      @pointerleave="active = undefined"
      @click="selectPoint"
    >
      <svg
        :width="Math.max(300, width)"
        :height="Math.max(150, height)"
        class="block max-w-full"
        role="img"
        :aria-label="`${cityMetricLabel(metric)} trend`"
      >
        <title>{{ cityMetricLabel(metric) }} over recorded city time</title>
        <desc>Line connects recorded readings. Hover or use Left and Right to inspect; Enter selects a city time.</desc>
        <g v-for="tick in chart.yTicks" :key="tick.value">
          <line :x1="chart.left" :y1="tick.y" :x2="chart.right" :y2="tick.y" stroke="#262626" />
          <text :x="chart.left - 10" :y="tick.y + 4" text-anchor="end" fill="#737373" font-size="10">
            {{ cityNumber(tick.value, true) }}
          </text>
        </g>
        <polyline
          v-if="chart.points.length > 1"
          data-city-line
          :points="chart.polyline"
          fill="none"
          stroke="#f9a8d4"
          stroke-width="2"
          stroke-linejoin="round"
          stroke-linecap="round"
        />
        <circle
          v-for="point in chart.points.length <= 20 ? chart.points : []"
          :key="point.frame"
          :cx="point.x"
          :cy="point.y"
          r="2.5"
          fill="#f9a8d4"
        />
        <g v-if="cursor">
          <line
            :x1="cursor.x"
            :y1="chart.top"
            :x2="cursor.x"
            :y2="chart.bottom"
            stroke="#a3a3a3"
            stroke-dasharray="3 4"
          />
          <circle :cx="cursor.x" :cy="cursor.y" r="4" fill="#f9a8d4" stroke="#171717" stroke-width="2" />
        </g>
        <text
          v-for="(tick, index) in chart.xTicks"
          :key="tick.frame"
          :x="tick.x"
          :y="chart.bottom + 21"
          :text-anchor="index === 0 ? 'start' : index === chart.xTicks.length - 1 ? 'end' : 'middle'"
          fill="#737373"
          font-size="10"
        >
          {{ tick.label }}
        </text>
        <g
          v-for="action in showActions ? actions : []"
          :key="action.index"
          role="button"
          tabindex="0"
          :aria-label="`${action.count} city actions: ${action.label}`"
          class="cursor-pointer"
          @click.stop="emit('record', action.index)"
          @keydown.enter.stop="emit('record', action.index)"
        >
          <title>{{ action.label }}{{ action.count > 1 ? ` and ${action.count - 1} more` : '' }}</title>
          <circle :cx="action.x" :cy="chart.bottom - 8" r="6" fill="#fbbf24" stroke="#171717" stroke-width="2" />
        </g>
      </svg>
    </div>
    <div class="flex flex-wrap items-center justify-between gap-2 pt-2 text-xs text-neutral-500">
      <div class="flex flex-wrap items-center gap-2">
        <span>{{ total }} {{ total === 1 ? 'reading' : 'readings' }}</span
        ><select
          v-if="total > 1"
          v-model="windowSize"
          aria-label="Trend range"
          class="rounded bg-neutral-900 px-2 py-1 text-neutral-400"
          @change="emit('range', windowSize === 'all' ? undefined : Number(windowSize))"
        >
          <option value="all">Available history</option>
          <option value="50">Last 50 readings</option>
          <option value="200">Last 200 readings</option></select
        ><span v-else>More readings needed for a trend</span
        ><button
          class="flex items-center gap-1 rounded px-1.5 py-1 hover:bg-neutral-800"
          :class="showActions ? 'text-amber-300' : ''"
          :aria-pressed="showActions"
          @click="showActions = !showActions"
        >
          <span class="i-mingcute:building-2-line size-3.5" aria-hidden="true" />Actions
        </button>
      </div>
      <button
        class="flex items-center gap-1 rounded px-1.5 py-1 text-pink-300 hover:bg-neutral-800 disabled:text-neutral-600"
        aria-label="Open trend evidence"
        :disabled="!current"
        @click="current && emit('record', current.index)"
      >
        <span class="i-mingcute:file-search-line size-3.5" aria-hidden="true" />Source
      </button>
    </div>
  </section>
</template>
