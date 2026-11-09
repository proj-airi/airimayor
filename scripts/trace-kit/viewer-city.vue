<script setup lang="ts">
import type { CityCategory, CityMetric, CityQuery, CityView } from './city-view'
import type { ViewerClient } from './viewer-client'
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import {
  cityDate,
  cityMetricLabel,
  cityNumber,
  cityRoads,
  cityValue,
  defaultCityMetric,
  trafficMeasures,
} from './city-chart'
import Trend from './viewer-city-chart.vue'
import Picker from './viewer-city-picker.vue'

const props = defineProps<{ client: ViewerClient; position: number }>()
const emit = defineEmits<{ record: [index: number] }>()
const result = shallowRef<CityView>(),
  category = ref<CityCategory>('Population'),
  metric = ref(''),
  road = ref(''),
  trafficField = ref('flowPercent'),
  frame = ref<number>(),
  offset = ref(0),
  eventOffset = ref(0),
  panel = ref(''),
  chartLimit = ref<number>(),
  error = ref(''),
  loading = ref(false),
  imageIndex = ref<number>(),
  imageUrl = ref('')
let desired: { client: ViewerClient; position: number; query: CityQuery; serial: number } | undefined,
  pending = false,
  serial = 0,
  disposed = false
watch(
  metric,
  () => {
    offset.value = 0
  },
  { flush: 'sync' },
)
watch(
  frame,
  () => {
    eventOffset.value = 0
  },
  { flush: 'sync' },
)
watch(
  () => props.position,
  (next, previous) => {
    if (next < previous) {
      offset.value = 0
      eventOffset.value = 0
    }
  },
)
watch(
  () =>
    [
      props.client,
      props.position,
      metric.value,
      frame.value,
      offset.value,
      eventOffset.value,
      chartLimit.value,
    ] as const,
  async ([client, position, key, at, start, eventStart, chartLimit]) => {
    desired = {
      client,
      position,
      query: { metric: key, frame: at, offset: start, eventOffset: eventStart, chartLimit },
      serial: ++serial,
    }
    if (pending) return
    pending = true
    loading.value = true
    try {
      while (desired && !disposed) {
        const current = desired
        desired = undefined
        const next = await current.client.remote.city(current.position, current.query)
        if (!disposed && current.serial === serial) {
          result.value = next
          error.value = ''
        }
      }
    } catch (value) {
      if (!disposed) error.value = String(value)
    } finally {
      pending = false
      if (!disposed) loading.value = false
    }
  },
  { immediate: true },
)
onBeforeUnmount(() => {
  disposed = true
  desired = undefined
})

const topics: { key: CityCategory; label: string; icon: string }[] = [
  { key: 'Population', label: 'Population', icon: 'i-mingcute:group-line' },
  { key: 'Finance', label: 'Finances', icon: 'i-mingcute:wallet-line' },
  { key: 'Demand', label: 'Demand', icon: 'i-mingcute:building-1-line' },
  { key: 'Services', label: 'Services', icon: 'i-mingcute:flash-line' },
  { key: 'Traffic', label: 'Traffic', icon: 'i-mingcute:road-line' },
  { key: 'Notifications', label: 'Notifications', icon: 'i-mingcute:notification-line' },
  { key: 'Construction', label: 'Construction', icon: 'i-mingcute:building-2-line' },
  { key: 'Statistics', label: 'Statistics', icon: 'i-mingcute:chart-line-line' },
]
const availableTopics = computed(() =>
  topics.filter((topic) => result.value?.metrics.some((item) => item.category === topic.key)),
)
const measures = computed(
  () =>
    result.value?.metrics
      .filter((item) => item.category === category.value)
      .map((item) => ({ key: item.key, label: cityMetricLabel(item) })) ?? [],
)
const roads = computed(() => cityRoads(result.value?.metrics ?? []))
const preferred = new Map<CityCategory, string>()
watch(metric, (key) => {
  const item = result.value?.metrics.find((item) => item.key === key)
  if (item) preferred.set(item.category, key)
})
watch([category, road, trafficField, () => result.value?.metrics], () => {
  const metrics = result.value?.metrics ?? []
  if (!metrics.length) return
  if (!metrics.some((item) => item.category === category.value)) category.value = availableTopics.value[0].key
  if (category.value === 'Traffic') {
    if (!roads.value.some((item) => item.key === road.value)) road.value = roads.value[0]?.key ?? ''
    metric.value = defaultCityMetric(metrics, 'Traffic', `road.${road.value}.${trafficField.value}`)?.key ?? ''
  } else
    metric.value = defaultCityMetric(metrics, category.value, preferred.get(category.value) ?? metric.value)?.key ?? ''
})
const cards = computed(() =>
  [
    ['population', 'Population', 'Population', 'citizens', 'i-mingcute:group-line'],
    ['treasury', 'Treasury', 'Finance', 'currency', 'i-mingcute:wallet-line'],
    ['budget.balance', 'Monthly balance', 'Finance', 'currency / month', 'i-mingcute:bank-line'],
    ['notifications.total', 'Notifications', 'Notifications', 'notifications', 'i-mingcute:notification-line'],
  ].map(([key, label, category, unit, icon]) => ({
    ...(result.value?.metrics.find((item) => item.key === key) ?? {
      key,
      label,
      category: category as CityCategory,
      unit,
      samples: 0,
    }),
    label,
    icon,
  })),
)
const selectedFramePosition = computed(() => {
  const frames = result.value?.frames ?? [],
    current = result.value?.cursor?.frame
  const found = frames.findIndex((stamp) => stamp.frame === current)
  return found < 0 ? Math.max(0, frames.length - 1) : found
})
const sourceNames = {
  read: 'City reading',
  history: 'Game history',
  before: 'Before action',
  after: 'After action',
}
const panels = computed(() =>
  [
    {
      key: 'readings',
      label: 'Readings',
      icon: 'i-mingcute:table-2-line',
      available: !!result.value?.selected,
    },
    {
      key: 'actions',
      label: `Actions · ${cityNumber(result.value?.eventTotal)}`,
      icon: 'i-mingcute:building-2-line',
      available: !!result.value?.eventTotal,
    },
    {
      key: 'notifications',
      label: 'Notification details',
      icon: 'i-mingcute:notification-line',
      available: !!result.value?.notifications,
    },
    {
      key: 'images',
      label: 'City images',
      icon: 'i-mingcute:pic-line',
      available: !!result.value?.media.length,
    },
  ].filter((item) => item.available),
)
watch(
  () => result.value?.media,
  (media) => {
    if (!media?.some((item) => item.index === imageIndex.value)) imageIndex.value = media?.[0]?.index
  },
)
watch(
  () => [props.client, panel.value, imageIndex.value] as const,
  async ([client, active, index], _old, onCleanup) => {
    let current = true,
      url = ''
    imageUrl.value = ''
    onCleanup(() => {
      current = false
      if (url) URL.revokeObjectURL(url)
    })
    if (active !== 'images' || index === undefined) return
    try {
      const attachment = await client.remote.cityImage(index)
      if (attachment && current) {
        url = URL.createObjectURL(attachment.blob)
        imageUrl.value = url
      }
    } catch (value) {
      if (current) error.value = String(value)
    }
  },
)
function chooseMetric(card: CityMetric) {
  preferred.set(card.category, card.key)
  category.value = card.category
  metric.value = card.key
}
function chooseFrame(index: number) {
  frame.value = result.value?.frames[index]?.frame
}
</script>

<template>
  <section aria-label="City dashboard" class="space-y-3 p-3 text-sm">
    <div class="flex flex-wrap items-center justify-between gap-2 text-xs">
      <h2 class="flex items-center gap-2 font-medium">
        <span class="i-mingcute:time-line size-4 text-neutral-500" aria-hidden="true" />{{ cityDate(result?.cursor) }}
      </h2>
      <button
        class="rounded px-2 py-1 text-neutral-400 hover:bg-neutral-800 disabled:text-neutral-500"
        :disabled="frame === undefined"
        @click="frame = undefined"
      >
        {{ frame === undefined ? 'Following replay' : 'Return to replay' }}
      </button>
    </div>
    <p v-if="error" class="text-red-400" role="alert">{{ error }}</p>
    <p v-if="loading && !result" class="text-neutral-400" role="status">Reading city observations…</p>
    <div
      v-if="result && !result.metrics.length"
      class="flex flex-col items-center gap-3 py-16 text-neutral-500"
      role="status"
    >
      <span class="i-mingcute:building-1-line size-8" aria-hidden="true" />
      <p>No city observations yet.</p>
      <p class="text-xs">Move the replay forward to a recorded city reading.</p>
    </div>
    <template v-if="result?.metrics.length">
      <div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <button
          v-for="card in cards"
          :key="card.key"
          class="min-w-0 rounded-md border bg-neutral-900/50 px-3 py-2 text-left hover:bg-neutral-800 disabled:cursor-default disabled:opacity-50"
          :class="metric === card.key ? 'border-pink-300/50' : 'border-neutral-800'"
          :aria-label="card.label"
          :aria-description="`${cityValue(card.latest?.value, card.unit)} · ${cityDate(card.latest)}`"
          :aria-pressed="metric === card.key"
          :disabled="card.samples === 0"
          :title="`${card.label}: ${cityValue(card.latest?.value, card.unit)} · ${cityDate(card.latest)}`"
          @click="chooseMetric(card)"
        >
          <span class="flex items-center gap-1.5 text-xs text-neutral-400"
            ><span :class="card.icon" class="size-3.5 shrink-0" aria-hidden="true" />{{ card.label }}</span
          ><span class="mt-1 block truncate text-lg tabular-nums sm:text-xl">{{
            cityValue(card.latest?.value, card.unit, true)
          }}</span
          ><span class="mt-1 block truncate text-[10px] text-neutral-500">{{ cityDate(card.latest) }}</span>
        </button>
      </div>
      <nav class="flex gap-1 overflow-x-auto border-b border-neutral-800" role="tablist" aria-label="City topics">
        <button
          v-for="topic in availableTopics"
          :key="topic.key"
          role="tab"
          :aria-selected="category === topic.key"
          class="flex shrink-0 items-center gap-1.5 whitespace-nowrap border-b-2 px-2 py-2 text-xs"
          :class="
            category === topic.key
              ? 'border-pink-300 text-pink-300'
              : 'border-transparent text-neutral-400 hover:text-neutral-200'
          "
          @click="category = topic.key"
        >
          <span :class="topic.icon" class="size-3.5" aria-hidden="true" />{{ topic.label }}
        </button>
      </nav>
      <div v-if="category === 'Traffic'" class="grid grid-cols-[minmax(0,1fr)_auto] gap-2">
        <Picker v-model="road" label="Road" :items="roads" />
        <select
          v-model="trafficField"
          aria-label="Traffic measure"
          class="min-w-0 rounded-md border border-neutral-700 bg-neutral-900 px-2 text-xs"
        >
          <option v-for="item in trafficMeasures" :key="item.key" :value="item.key">{{ item.label }}</option>
        </select>
      </div>
      <Picker v-else v-model="metric" label="City measure" :items="measures" />
      <Trend
        v-if="result.selected"
        :metric="result.selected"
        :points="result.chart"
        :total="result.pointTotal"
        :clocks="result.frames"
        :frame="result.cursor?.frame"
        :markers="result.markers"
        @frame="frame = $event"
        @range="chartLimit = $event"
        @record="emit('record', $event)"
      />
      <div v-if="result.frames.length" class="flex items-center gap-2 border-b border-neutral-800 pb-3">
        <button
          class="viewer-icon-button"
          aria-label="Previous city time"
          :disabled="selectedFramePosition === 0"
          @click="chooseFrame(selectedFramePosition - 1)"
        >
          <span class="i-mingcute:left-line size-4" aria-hidden="true" />
        </button>
        <label class="flex min-w-0 flex-1 items-center gap-3 text-xs text-neutral-500"
          ><span class="shrink-0">City time</span
          ><input
            type="range"
            aria-label="City time"
            :min="0"
            :max="result.frames.length - 1"
            :value="selectedFramePosition"
            class="min-w-0 flex-1 accent-pink-300"
            @input="chooseFrame(Number(($event.target as HTMLInputElement).value))"
        /></label>
        <button
          class="viewer-icon-button"
          aria-label="Next city time"
          :disabled="selectedFramePosition === result.frames.length - 1"
          @click="chooseFrame(selectedFramePosition + 1)"
        >
          <span class="i-mingcute:right-line size-4" aria-hidden="true" />
        </button>
      </div>
      <div class="flex flex-wrap gap-1">
        <button
          v-for="item in panels"
          :key="item.key"
          :aria-expanded="panel === item.key"
          class="flex items-center gap-1.5 rounded px-2 py-1.5 text-xs hover:bg-neutral-800"
          :class="panel === item.key ? 'bg-neutral-800 text-neutral-200' : 'text-neutral-500'"
          @click="panel = panel === item.key ? '' : item.key"
        >
          <span :class="item.icon" class="size-3.5" aria-hidden="true" />{{ item.label }}
        </button>
      </div>
      <section
        v-if="panel === 'readings' && result.selected"
        aria-label="City readings"
        class="space-y-2 rounded-md border border-neutral-800 p-3"
      >
        <div class="max-h-72 overflow-auto">
          <table class="w-full whitespace-nowrap text-left text-xs tabular-nums">
            <thead class="text-neutral-500">
              <tr>
                <th class="p-2">City time</th>
                <th class="px-2">Value</th>
                <th class="px-2">Reading</th>
                <th class="px-2">Source</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="point in result.points"
                :key="`${point.frame}:${point.index}`"
                class="border-t border-neutral-800"
                :class="point.frame > (result.cursor?.frame ?? Infinity) ? 'text-neutral-500' : ''"
              >
                <td class="p-2">
                  <button
                    class="hover:text-pink-300"
                    :title="`Simulation frame ${point.frame}`"
                    @click="frame = point.frame"
                  >
                    {{ cityDate(point) }}
                    <span v-if="!point.gameTime" class="text-neutral-500"> · frame {{ cityNumber(point.frame) }}</span>
                  </button>
                </td>
                <td class="px-2">{{ cityValue(point.value, result.selected.unit) }}</td>
                <td class="px-2">{{ sourceNames[point.kind] }}</td>
                <td class="px-2">
                  <button class="text-pink-300 hover:underline" @click="emit('record', point.index)">
                    Open source
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <div class="flex items-center justify-end gap-2 text-xs text-neutral-500">
          <button class="viewer-button py-1" :disabled="offset === 0" @click="offset -= 50">Previous readings</button
          ><span>{{ offset + 1 }}–{{ Math.min(offset + 50, result.pointTotal) }} / {{ result.pointTotal }}</span
          ><button class="viewer-button py-1" :disabled="offset + 50 >= result.pointTotal" @click="offset += 50">
            Next readings
          </button>
        </div>
      </section>
      <section
        v-if="panel === 'actions'"
        aria-label="City actions"
        class="space-y-2 rounded-md border border-neutral-800 p-3"
      >
        <div class="max-h-80 space-y-2 overflow-y-auto">
          <div v-for="event in result.events" :key="event.index" class="rounded bg-neutral-900 p-3">
            <div class="flex flex-wrap justify-between gap-2">
              <button class="text-left text-xs text-pink-300 hover:underline" @click="emit('record', event.index)">
                {{ event.label }}</button
              ><span class="text-xs text-neutral-500"
                >{{ event.status }}{{ event.verdict ? ` · ${event.verdict}` : '' }}</span
              >
            </div>
            <p class="mt-1 text-xs text-neutral-500">
              {{ cityDate(event.frame === undefined ? undefined : { frame: event.frame, gameTime: event.gameTime }) }}
            </p>
            <p v-for="change in event.changes" :key="change.field" class="mt-2 text-xs tabular-nums">
              {{ change.field }}: {{ cityNumber(change.before) }} → {{ cityNumber(change.after) }} ({{
                change.delta > 0 ? '+' : ''
              }}{{ cityNumber(change.delta) }})
            </p>
            <p v-if="event.note" class="mt-2 text-xs text-neutral-400">{{ event.note }}</p>
          </div>
        </div>
        <div class="flex items-center justify-end gap-2 text-xs text-neutral-500">
          <button class="viewer-button py-1" :disabled="eventOffset === 0" @click="eventOffset -= 30">
            Previous actions</button
          ><span
            >{{ result.eventTotal ? eventOffset + 1 : 0 }}–{{ Math.min(eventOffset + 30, result.eventTotal) }} /
            {{ result.eventTotal }}</span
          ><button
            class="viewer-button py-1"
            :disabled="eventOffset + 30 >= result.eventTotal"
            @click="eventOffset += 30"
          >
            Next actions
          </button>
        </div>
      </section>
      <section
        v-if="panel === 'notifications' && result.notifications"
        aria-label="Notification details"
        class="space-y-2 rounded-md border border-neutral-800 p-3"
      >
        <div class="flex flex-wrap justify-between gap-2 text-xs text-neutral-500">
          <span>{{ cityDate(result.notifications) }} · {{ cityNumber(result.notifications.total) }} citywide</span
          ><button class="text-pink-300 hover:underline" @click="emit('record', result.notifications.index)">
            Open notification source
          </button>
        </div>
        <p class="text-xs text-neutral-500">
          {{ result.notifications.items.length }} detail rows{{ result.notifications.truncated ? ' · truncated' : '' }}.
          Details follow the recorded filters and row limit.
        </p>
        <div class="max-h-72 overflow-auto">
          <table class="w-full whitespace-nowrap text-left text-xs">
            <thead class="text-neutral-500">
              <tr>
                <th class="p-2">Notification</th>
                <th class="px-2">Priority</th>
                <th class="px-2">Place</th>
                <th class="px-2">Location</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(item, index) in result.notifications.items" :key="index" class="border-t border-neutral-800">
                <td class="p-2">{{ item.type }}</td>
                <td class="px-2">{{ cityNumber(item.priority) }}</td>
                <td class="px-2">{{ item.target }}</td>
                <td class="px-2">{{ item.location }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
      <section
        v-if="panel === 'images' && result.media.length"
        aria-label="City images"
        class="space-y-2 rounded-md border border-neutral-800 p-3"
      >
        <div class="flex flex-wrap items-center justify-between gap-2">
          <select
            v-model="imageIndex"
            aria-label="City image"
            class="max-w-full rounded border border-neutral-700 bg-neutral-900 px-2 py-1 text-xs"
          >
            <option v-for="item in result.media" :key="item.index" :value="item.index">
              {{ item.tool === 'map_image' ? 'Map' : 'Screenshot' }} ·
              {{ cityDate(item.frame === undefined ? undefined : { frame: item.frame, gameTime: item.gameTime }) }}
            </option></select
          ><button
            v-if="imageIndex !== undefined"
            class="text-xs text-pink-300 hover:underline"
            @click="emit('record', imageIndex)"
          >
            Open image source
          </button>
        </div>
        <img
          v-if="imageUrl"
          :src="imageUrl"
          alt="Recorded city view at the selected city time"
          class="max-h-96 max-w-full rounded object-contain"
        />
        <p v-else class="text-xs text-neutral-500">Image unavailable or loading…</p>
      </section>
      <details class="text-xs text-neutral-500">
        <summary class="cursor-pointer">
          About these readings<span v-if="result.warnings.length" class="ml-2 text-amber-300"
            >{{ result.warnings.length }} data issues</span
          >
        </summary>
        <p class="mt-2">
          Lines join recorded readings; no readings are added between them. Each measure uses its last observation at
          the selected city time. Only data acquired by the replay is available.
        </p>
        <p v-if="result.selected?.scope" class="mt-2">{{ result.selected.scope }}</p>
        <p v-if="result.chart.length < result.pointTotal" class="mt-2">
          {{ result.chart.length }} points shown in this range. Long series retain local highs and lows. All
          {{ result.pointTotal }} readings remain in the table.
        </p>
        <p class="mt-2">
          {{ result.observations }} successful city reads. {{ result.untimed }} without city time remain available in
          source evidence.
        </p>
        <p v-for="warning in result.warnings" :key="warning" class="mt-2 text-amber-300">{{ warning }}</p>
      </details>
    </template>
  </section>
</template>
