<script setup lang="ts">
import type { EChartsType } from 'echarts/core'
import type { TimelineCanvasFrame } from '@proj-airi/ui-timeline/headless'
import type { CityMetric, CityReplayPoint, CityOverview } from './city-view'
import { computed, onBeforeUnmount, onMounted, useTemplateRef, watch } from 'vue'
import { init, use } from 'echarts/core'
import { LineChart, ScatterChart } from 'echarts/charts'
import { GridComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { TimelineCanvas, TimelineLanes, injectTimelineRootContext } from '@proj-airi/ui-timeline/headless'
import { timeToX } from '@proj-airi/timeline-canvas'
import { cityMetricLabel, cityNumber } from './city-chart'

use([LineChart, ScatterChart, GridComponent, CanvasRenderer])
const props = defineProps<{
  metric: CityMetric
  points: CityReplayPoint[]
  total: number
  index: number
  markers: CityOverview['markers']
  showActions: boolean
}>()
const emit = defineEmits<{ hover: [point: CityReplayPoint | undefined]; record: [index: number] }>()
const context = injectTimelineRootContext()
const root = useTemplateRef<HTMLElement>('root')
const actions = computed(() => props.markers.filter((event) => event.index <= props.index))
const floor = Math.min(...props.points.map((point) => point.value))
let plot: EChartsType | undefined
let hovered = -1,
  fitted = false
let lastView = '',
  size = ''

function inspect(x: number, y: number) {
  if (!plot?.containPixel({ gridIndex: 0 }, [x, y])) {
    clearHover()
    return
  }
  const time = context.timeAt(root.value!.getBoundingClientRect().left + x)
  let nearest = 0
  for (let index = 1; index < props.points.length; index++)
    if (Math.abs(props.points[index].time - time) < Math.abs(props.points[nearest].time - time)) nearest = index
  if (nearest !== hovered) {
    hovered = nearest
    emit('hover', props.points[nearest])
  }
}
function clearHover() {
  if (hovered < 0) return
  hovered = -1
  emit('hover', undefined)
}
function hitAction(event: PointerEvent) {
  if (!props.showActions || !plot) return
  const rect = root.value!.getBoundingClientRect()
  const item = actions.value.find((action) => {
    const [x, y] = plot!.convertToPixel({ gridIndex: 0 }, [action.time, floor]) as number[]
    return Math.hypot(event.clientX - rect.left - x, event.clientY - rect.top - y) <= 8
  })
  return item && { kind: 'record', key: String(item.index) }
}

function draw(ctx: CanvasRenderingContext2D, frame: TimelineCanvasFrame) {
  if (!plot) return
  if (!fitted) {
    context.fit()
    fitted = true
    return
  }
  const dimensions = `${frame.width}:${frame.height}`
  if (dimensions !== size) {
    plot.resize({ width: frame.width, height: frame.height })
    size = dimensions
  }
  const view = `${frame.view.start}:${frame.view.end}:${frame.width}`
  if (view !== lastView) {
    plot.setOption({ xAxis: { min: context.visibleStart(), max: frame.view.end } })
    lastView = view
  }
  const cursor = context.cursor.value
  if (cursor === undefined) return
  const x = Math.round(timeToX(frame.view, cursor)) + 0.5
  if (x < 60 || x > frame.width) return
  ctx.strokeStyle = '#38bdf8'
  ctx.lineWidth = 1
  ctx.beginPath()
  ctx.moveTo(x, 0)
  ctx.lineTo(x, frame.height)
  ctx.stroke()
}
function updateActions() {
  plot?.setOption({
    series: [{ id: 'actions', data: props.showActions ? actions.value.map((event) => [event.time, floor]) : [] }],
  })
}
onMounted(() => {
  plot = init(root.value!, undefined, { renderer: 'canvas' })
  const color = getComputedStyle(root.value!).color
  const data = props.points.map((point) => ({
    value: [point.time, point.value],
    symbolSize: props.points.length <= 20 ? 4 : 0,
  }))
  const last = props.points.at(-1)
  const end = context.origin.value + context.span.value
  if (last && last.time < end) data.push({ value: [end, last.value], symbolSize: 0 })
  plot.setOption({
    animation: false,
    textStyle: { fontFamily: getComputedStyle(root.value!).fontFamily },
    grid: { left: 60, right: 0, top: 12, bottom: 12 },
    xAxis: { type: 'value', show: false },
    yAxis: {
      type: 'value',
      scale: true,
      splitNumber: 4,
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { fontSize: 10, color: '#737373', formatter: (value: number) => cityNumber(value, true) },
      splitLine: { lineStyle: { color: '#262626' } },
    },
    series: [
      {
        id: 'trend',
        name: cityMetricLabel(props.metric),
        type: 'line',
        step: 'end',
        data,
        showSymbol: props.points.length <= 20,
        symbolSize: 4,
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
      },
    ],
  })
  plot.getZr().on('mousemove', (event) => inspect(event.offsetX, event.offsetY))
  plot.getZr().on('globalout', clearHover)
  plot.on('click', { seriesId: 'actions' }, (event) => emit('record', actions.value[event.dataIndex].index))
  updateActions()
})
watch(() => (props.showActions ? actions.value.length : 0), updateActions)
onBeforeUnmount(() => plot?.dispose())
</script>

<template>
  <TimelineLanes
    :hit-test="hitAction"
    role="img"
    :aria-label="`${cityMetricLabel(metric)} trend`"
    :aria-description="`${points.length} plotted points from ${total} observations in the full recording. Uses the same recording clock and timeline controls as Timeline.`"
    class="relative min-h-0 flex-1 touch-none overflow-hidden"
    @pointerleave="clearHover"
  >
    <div ref="root" class="absolute inset-0 text-primary-300" />
    <TimelineCanvas data-city-playhead :draw="draw" class="pointer-events-none absolute inset-0" />
  </TimelineLanes>
</template>
