<script setup lang="ts">
import type { CityEvent, CityMetric, CityPoint, CityStamp } from './city-view'
import type { EChartsType } from 'echarts/core'
import { computed, onBeforeUnmount, onMounted, ref, useTemplateRef, watch } from 'vue'
import { useElementSize } from '@vueuse/core'
import { init, use } from 'echarts/core'
import { LineChart, ScatterChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, MarkLineComponent, MarkPointComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { cityDate, cityMetricLabel, cityNumber, cityTrendRange, cityTrendTicks, cityValue } from './city-chart'

use([LineChart, ScatterChart, GridComponent, TooltipComponent, MarkLineComponent, MarkPointComponent, CanvasRenderer])
const props = defineProps<{
  metric: CityMetric
  current?: CityMetric
  points: CityPoint[]
  total: number
  clocks: CityStamp[]
  frame?: number
  index: number
  limit?: number
  markers: Pick<CityEvent, 'index' | 'label' | 'status' | 'frame'>[]
}>()
const emit = defineEmits<{
  seek: [index: number]
  record: [index: number]
  range: [limit: number | undefined]
}>()
const root = useTemplateRef<HTMLElement>('root')
const { width, height } = useElementSize(root)
const active = ref<number>(),
  showActions = ref(false),
  windowSize = ref(props.limit === undefined ? 'all' : String(props.limit))
const current = computed(() => (active.value === undefined ? props.current?.latest : props.points[active.value]))
const preview = computed(() => current.value !== undefined && current.value.index > props.index)
const outside = computed(() => current.value && (current.value.frame < start || current.value.frame > end))
const { start, end } = cityTrendRange(props.points, props.clocks)
const actionPoints = computed(() =>
  props.markers.filter(
    (event) => event.index <= props.index && event.frame !== undefined && event.frame >= start && event.frame <= end,
  ),
)
let plot: EChartsType | undefined

function axisLabels() {
  const ticks = cityTrendTicks(props.points, props.clocks, width.value)
  return {
    fontSize: 10,
    color: '#737373',
    hideOverlap: true,
    customValues: ticks.map((tick) => tick.frame),
    formatter: (value: number) => ticks.find((tick) => tick.frame === value)?.label ?? '',
  }
}
function updateCursor() {
  if (!plot) return
  const frame = props.frame
  const value = current.value
  plot.setOption({
    series: [
      {
        id: 'trend',
        markLine: {
          silent: true,
          symbol: 'none',
          label: { show: false },
          lineStyle: { color: '#a3a3a3', width: 1, type: 'dashed' },
          data: frame !== undefined && frame >= start && frame <= end ? [{ xAxis: frame }] : [],
        },
        markPoint: {
          silent: true,
          symbol: 'circle',
          symbolSize: 8,
          label: { show: false },
          itemStyle: { color: getComputedStyle(root.value!).color, borderColor: '#171717', borderWidth: 2 },
          data: value && !outside.value ? [{ coord: [value.frame, value.value] }] : [],
        },
      },
    ],
  })
}
function inspect(x: number, y: number) {
  if (!plot || !plot.containPixel({ gridIndex: 0 }, [x, y]) || !props.points.length) {
    active.value = undefined
    return
  }
  const [frame] = plot.convertFromPixel({ gridIndex: 0 }, [x, y]) as number[]
  let nearest = 0
  for (let index = 1; index < props.points.length; index++)
    if (Math.abs(props.points[index].frame - frame) < Math.abs(props.points[nearest].frame - frame)) nearest = index
  active.value = nearest
}
function step(direction: number) {
  const index =
    active.value ??
    Math.max(
      0,
      props.points.findIndex((point) => point.frame === current.value?.frame),
    )
  active.value = Math.max(0, Math.min(props.points.length - 1, index + direction))
}
onMounted(() => {
  plot = init(root.value!, undefined, { renderer: 'canvas' })
  const color = getComputedStyle(root.value!).color
  plot.setOption({
    animation: false,
    textStyle: { fontFamily: getComputedStyle(root.value!).fontFamily },
    grid: { left: 60, right: 18, top: 18, bottom: 30 },
    xAxis: {
      type: 'value',
      min: start === end ? start - 1 : start,
      max: end === start ? end + 1 : end,
      axisLine: { show: false },
      axisTick: { show: false },
      splitLine: { show: false },
      axisLabel: axisLabels(),
    },
    yAxis: {
      type: 'value',
      scale: true,
      splitNumber: 4,
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { fontSize: 10, color: '#737373', formatter: (value: number) => cityNumber(value, true) },
      splitLine: { lineStyle: { color: '#262626' } },
    },
    tooltip: {
      trigger: 'axis',
      renderMode: 'richText',
      confine: true,
      backgroundColor: '#171717',
      borderColor: '#404040',
      textStyle: { color: '#e5e5e5', fontSize: 11 },
      formatter: (parameters: { dataIndex: number } | { dataIndex: number }[]) => {
        const point = props.points[(Array.isArray(parameters) ? parameters[0] : parameters).dataIndex]
        return `${cityMetricLabel(props.metric)}\n${cityDate(point)}\n${cityValue(point.value, props.metric.unit)}${point.index > props.index ? '\nPreview ahead of replay' : ''}`
      },
    },
    series: [
      {
        id: 'trend',
        name: cityMetricLabel(props.metric),
        type: 'line',
        data: props.points.map((point) => [point.frame, point.value]),
        showSymbol: props.points.length <= 20,
        symbolSize: 4,
        triggerLineEvent: true,
        lineStyle: { color, width: 2 },
        itemStyle: { color },
        emphasis: { disabled: true },
      },
      {
        id: 'actions',
        type: 'scatter',
        data: [],
        symbol: 'triangle',
        symbolSize: 10,
        itemStyle: { color: '#fbbf24' },
        tooltip: { show: false },
      },
    ],
  })
  plot.getZr().on('mousemove', (event) => inspect(event.offsetX, event.offsetY))
  plot.getZr().on('click', (event) => {
    if (event.target) return
    inspect(event.offsetX, event.offsetY)
    if (active.value !== undefined) emit('seek', props.points[active.value].index)
  })
  plot.on('click', (event) => {
    if (event.seriesId === 'actions') emit('record', actionPoints.value[event.dataIndex].index)
    else if (event.seriesId === 'trend' && event.event) {
      inspect(event.event.offsetX, event.event.offsetY)
      if (active.value !== undefined) emit('seek', props.points[active.value].index)
    }
  })
  updateCursor()
})
watch([width, height], () => {
  plot?.resize()
  plot?.setOption({ xAxis: { axisLabel: axisLabels() } })
  updateCursor()
})
watch([() => props.frame, () => props.current, active], updateCursor)
watch(
  () => props.index,
  () => {
    active.value = undefined
  },
)
watch([showActions, () => props.index], ([show]) =>
  plot?.setOption({
    series: [
      {
        id: 'actions',
        data: show
          ? actionPoints.value.map((event) => [event.frame, Math.min(...props.points.map((point) => point.value))])
          : [],
      },
    ],
  }),
)
onBeforeUnmount(() => plot?.dispose())
</script>

<template>
  <section aria-label="City trend" class="min-w-0">
    <div class="flex min-h-12 flex-wrap items-start justify-between gap-2 px-1 pb-2">
      <div>
        <h3 class="text-sm font-medium">{{ cityMetricLabel(metric) }}</h3>
        <p class="mt-0.5 text-xs text-neutral-500">
          {{ current ? cityDate(current) : 'No reading reached at replay position' }}
          <span v-if="preview"> · preview ahead of replay</span>
          <span v-else-if="outside"> · outside the shown range</span>
        </p>
      </div>
      <span data-city-active-value class="text-lg tabular-nums">{{ cityValue(current?.value, metric.unit) }}</span>
    </div>
    <div
      tabindex="0"
      role="group"
      :aria-label="`${cityMetricLabel(metric)} chart controls`"
      class="h-44 w-full rounded-md border border-neutral-800 bg-neutral-900/40 outline-none focus-visible:border-primary-300 sm:h-56 xl:h-64"
      @keydown.left.prevent="step(-1)"
      @keydown.right.prevent="step(1)"
      @keydown.enter.prevent="current && emit('seek', current.index)"
      @pointerleave="active = undefined"
    >
      <div
        ref="root"
        role="img"
        :aria-label="`${cityMetricLabel(metric)} trend`"
        :aria-description="`${points.length} plotted points from ${total} readings in the full recording. The playhead follows replay. Hover or use Left and Right to inspect; Enter seeks the main replay.`"
        class="h-full w-full text-primary-300"
      />
    </div>
    <div class="flex flex-wrap items-center justify-between gap-2 pt-2 text-xs text-neutral-500">
      <div class="flex flex-wrap items-center gap-2">
        <span>{{ total }} readings · full recording</span>
        <select
          v-if="total > 1"
          v-model="windowSize"
          aria-label="Trend range"
          class="rounded bg-neutral-900 px-2 py-1 text-neutral-400"
          @change="emit('range', windowSize === 'all' ? undefined : Number(windowSize))"
        >
          <option value="all">Available history</option>
          <option value="50">Last 50 readings</option>
          <option value="200">Last 200 readings</option>
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
