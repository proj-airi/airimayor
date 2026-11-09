<script setup lang="ts">
import type { CityCategory, CityMetric, CityQuery, CityStamp, CityView } from './city-view'
import type { ViewerClient } from './viewer-client'
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue'

const props = defineProps<{ client: ViewerClient; position: number }>()
const emit = defineEmits<{ record: [index: number] }>()
const result = shallowRef<CityView>(),
  category = ref<CityCategory>('Population'),
  metric = ref(''),
  frame = ref<number>(),
  offset = ref(0),
  eventOffset = ref(0),
  error = ref(''),
  loading = ref(false),
  imageIndex = ref<number>(),
  imageUrl = ref('')
let desired: { client: ViewerClient; position: number; query: CityQuery; serial: number } | undefined,
  pending = false,
  serial = 0,
  disposed = false
watch(
  [category, metric],
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
      frame.value = undefined
    }
  },
)
watch(
  () => [props.client, props.position, metric.value, frame.value, offset.value, eventOffset.value] as const,
  async ([client, position, key, at, start, eventStart]) => {
    desired = {
      client,
      position,
      query: { metric: key, frame: at, offset: start, eventOffset: eventStart },
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
const categories = computed(() => [...new Set(result.value?.metrics.map((metric) => metric.category) ?? [])])
const metrics = computed(() => result.value?.metrics.filter((metric) => metric.category === category.value) ?? [])
watch(categories, (values) => {
  if (values.length && !values.includes(category.value)) category.value = values[0]
})
watch(metrics, (values) => {
  if (!values.some((value) => value.key === metric.value)) metric.value = values[0]?.key ?? ''
})
const cards = computed(() =>
  [
    ['population', 'Population', 'Population', 'citizens'],
    ['treasury', 'Treasury', 'Finance', 'currency'],
    ['budget.balance', 'Monthly balance', 'Finance', 'currency / month'],
    ['labor.unemploymentRate', 'Unemployment rate', 'Population', 'native rate'],
    ['labor.homelessCitizens', 'Homeless citizens', 'Population', 'citizens'],
    ['notifications.total', 'City notifications', 'Notifications', 'notifications'],
    ['services.powerMargin', 'Power production + imports − consumption', 'Services', 'game units'],
    ['services.waterMargin', 'Fresh water capacity − consumption', 'Services', 'game units'],
    ['services.sewageMargin', 'Sewage capacity − consumption', 'Services', 'game units'],
  ].map(
    ([key, label, category, unit]): CityMetric =>
      result.value?.metrics.find((metric) => metric.key === key) ?? {
        key,
        label,
        category: category as CityCategory,
        unit,
        samples: 0,
      },
  ),
)
const selectedFramePosition = computed(() => {
  const frames = result.value?.frames ?? [],
    current = result.value?.cursor?.frame
  const found = frames.findIndex((stamp) => stamp.frame === current)
  return found < 0 ? Math.max(0, frames.length - 1) : found
})
const number = (value?: number) =>
  value === undefined ? 'No observation' : value.toLocaleString(undefined, { maximumFractionDigits: 2 })
const date = (stamp?: CityStamp) =>
  stamp ? `${stamp.gameTime ?? 'Game date unavailable'} · frame ${number(stamp.frame)}` : 'Game time unavailable'
const chart = computed(() => {
  const points = result.value?.chart ?? []
  const frames = result.value?.frames ?? []
  const minFrame = frames[0]?.frame ?? 0,
    maxFrame = frames.at(-1)?.frame ?? 1
  const values = points.map((point) => point.value),
    min = Math.min(...values),
    max = Math.max(...values)
  const y = (value: number) => 170 - ((value - min) / Math.max(1, max - min)) * 140
  const x = (frame: number) => 40 + ((frame - minFrame) / Math.max(1, maxFrame - minFrame)) * 720
  return {
    points: points.map((point) => ({ ...point, x: x(point.frame), y: y(point.value) })),
    min,
    max,
    cursor: result.value?.cursor ? x(result.value.cursor.frame) : undefined,
    events:
      result.value?.markers
        .filter((event) => event.frame !== undefined)
        .map((event) => ({ ...event, x: x(event.frame!) })) ?? [],
  }
})
watch(
  () => result.value?.media,
  (media) => {
    if (!media?.some((item) => item.index === imageIndex.value)) imageIndex.value = media?.[0]?.index
  },
)
watch(
  () => [props.client, imageIndex.value] as const,
  async ([client, index], _old, onCleanup) => {
    let current = true,
      url = ''
    imageUrl.value = ''
    onCleanup(() => {
      current = false
      if (url) URL.revokeObjectURL(url)
    })
    if (index === undefined) return
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
function chooseFrame(event: Event) {
  const index = Number((event.target as HTMLInputElement).value)
  frame.value = result.value?.frames[index]?.frame
}
function openPoint(index: number) {
  emit('record', index)
}
function chooseMetric(key: string, group: CityCategory) {
  category.value = group
  metric.value = key
}
</script>

<template>
  <div class="space-y-5 p-3 text-sm">
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div>
        <h3 class="font-medium">City at the replay cursor</h3>
        <p class="mt-1 text-xs text-neutral-400">{{ date(result?.cursor) }}</p>
      </div>
      <button class="viewer-button py-1 text-xs" :disabled="frame === undefined" @click="frame = undefined">
        Follow replay city time
      </button>
    </div>
    <p class="text-xs text-neutral-500">
      Only observations acquired by the replay cursor are available. Every reading keeps its game time and source;
      values between observations are unfilled.
    </p>
    <p v-if="error" class="text-red-400" role="alert">{{ error }}</p>
    <p v-if="loading && !result" class="text-neutral-400" role="status">Reading city observations…</p>
    <template v-if="result">
      <div v-if="result.frames.length" class="space-y-2">
        <label class="flex flex-col gap-2 text-xs text-neutral-400"
          >City time · {{ selectedFramePosition + 1 }} / {{ result.frames.length }} observed frames
          <input
            type="range"
            aria-label="City time"
            :min="0"
            :max="result.frames.length - 1"
            :value="selectedFramePosition"
            class="w-full accent-pink-300"
            @input="chooseFrame"
          />
        </label>
        <div class="flex justify-between gap-3 text-xs text-neutral-500">
          <span>{{ date(result.frames[0]) }}</span
          ><span class="text-right">{{ date(result.frames.at(-1)) }}</span>
        </div>
      </div>
      <div class="grid grid-cols-1 gap-2 sm:grid-cols-2 xl:grid-cols-3">
        <div v-for="card in cards" :key="card.key" class="min-w-0 rounded bg-neutral-900 p-3">
          <button
            class="text-left text-xs text-neutral-400 hover:text-pink-300"
            :disabled="card.samples === 0"
            @click="chooseMetric(card.key, card.category)"
          >
            {{ card.label }}
          </button>
          <p class="mt-1 tabular-nums">
            {{ number(card.latest?.value) }} <span class="text-xs text-neutral-500">{{ card.unit }}</span>
          </p>
          <p class="mt-1 text-xs text-neutral-500">
            {{ card.latest ? `Last observed: ${date(card.latest)}` : 'Not observed at this city time' }}
          </p>
          <button
            v-if="card.latest"
            class="mt-2 text-xs text-pink-300 hover:underline"
            @click="openPoint(card.latest.index)"
          >
            Open evidence
          </button>
        </div>
      </div>
      <p v-if="!result.metrics.length" class="rounded border border-neutral-800 p-5 text-neutral-400" role="status">
        No timed city readings are available at this replay cursor. City data appears when the recording contains
        successful city reads or write ledgers.
      </p>
      <section v-if="result.metrics.length" class="space-y-3">
        <div class="flex flex-wrap gap-2">
          <label class="flex min-w-0 flex-col gap-1 text-xs text-neutral-400"
            >Category<select
              v-model="category"
              aria-label="City category"
              class="rounded border border-neutral-700 bg-neutral-900 px-2 py-2"
            >
              <option v-for="item in categories" :key="item" :value="item">{{ item }}</option>
            </select></label
          >
          <label class="flex min-w-0 flex-1 flex-col gap-1 text-xs text-neutral-400"
            >City measure<select
              v-model="metric"
              aria-label="City measure"
              class="max-w-full rounded border border-neutral-700 bg-neutral-900 px-2 py-2"
            >
              <option v-for="item in metrics" :key="item.key" :value="item.key">{{ item.label }}</option>
            </select></label
          >
        </div>
        <div v-if="result.selected" class="flex flex-wrap items-start justify-between gap-2">
          <div>
            <h3 class="font-medium">{{ result.selected.label }}</h3>
            <p class="mt-1 text-xs text-neutral-400">
              {{ result.selected.unit }} · {{ result.pointTotal }} recorded frames · {{ result.selected.scope }}
            </p>
          </div>
          <div class="text-right tabular-nums">
            <div>{{ number(result.selected.latest?.value) }}</div>
            <div v-if="result.selected.latest && result.selected.previous" class="mt-1 text-xs text-neutral-400">
              Change since previous observation:
              {{ number(result.selected.latest.value - result.selected.previous.value) }}
            </div>
          </div>
        </div>
        <svg
          v-if="chart.points.length"
          viewBox="0 0 800 220"
          class="w-full rounded bg-neutral-900"
          role="img"
          :aria-label="`${result.selected?.label} observations by simulation frame`"
        >
          <line x1="40" y1="170" x2="760" y2="170" stroke="#525252" />
          <text x="40" y="15" fill="#a3a3a3" font-size="10">{{ number(chart.max) }}</text>
          <text x="40" y="190" fill="#a3a3a3" font-size="10">{{ number(chart.min) }}</text>
          <line
            v-if="chart.cursor !== undefined"
            :x1="chart.cursor"
            y1="20"
            :x2="chart.cursor"
            y2="175"
            stroke="#d4d4d4"
            stroke-dasharray="4 3"
          />
          <g
            v-for="point in chart.points"
            :key="`${point.frame}:${point.index}`"
            tabindex="0"
            role="button"
            class="cursor-pointer"
            :aria-label="`${number(point.value)} at ${date(point)}`"
            @click="frame = point.frame"
            @keydown.enter="frame = point.frame"
          >
            <title>{{ number(point.value) }} · {{ date(point) }} · {{ point.kind }}</title>
            <line :x1="point.x" y1="170" :x2="point.x" :y2="point.y" stroke="#f9a8d4" stroke-opacity="0.2" />
            <circle :cx="point.x" :cy="point.y" r="3.5" :fill="point.kind === 'history' ? '#6ee7b7' : '#f9a8d4'" />
          </g>
          <g
            v-for="event in chart.events"
            :key="event.index"
            tabindex="0"
            role="button"
            class="cursor-pointer"
            :aria-label="event.label"
            @click="openPoint(event.index)"
            @keydown.enter="openPoint(event.index)"
          >
            <title>{{ event.label }} · {{ event.status }}</title>
            <path :d="`M ${event.x} 199 l -4 8 h 8 z`" fill="#fbbf24" />
          </g>
        </svg>
        <p class="text-xs text-neutral-500">
          Pink: direct reading or ledger · green: native history · amber: city action · dashed line: selected city time.
          Horizontal spacing follows simulation time; paused calls can share the same frame.
          {{
            result.chart.length < result.pointTotal
              ? `Chart shows ${result.chart.length} selected points; the table retains all ${result.pointTotal}.`
              : ''
          }}
        </p>
        <p v-if="result.markers.length < result.eventTotal" class="text-xs text-neutral-500">
          {{ result.markers.length }} action markers are shown; all {{ result.eventTotal }} actions remain in the paged
          list.
        </p>
        <div class="overflow-x-auto">
          <table class="w-full whitespace-nowrap text-left text-xs tabular-nums">
            <thead class="text-neutral-400">
              <tr>
                <th class="p-2">Game time / frame</th>
                <th class="px-2">Value</th>
                <th class="px-2">Source</th>
                <th class="px-2">Evidence</th>
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
                  <button class="hover:text-pink-300" @click="frame = point.frame">{{ date(point) }}</button>
                </td>
                <td class="px-2">{{ number(point.value) }}</td>
                <td class="px-2">{{ point.kind }}</td>
                <td class="px-2">
                  <button class="text-pink-300 hover:underline" @click="openPoint(point.index)">Open evidence</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <div class="flex items-center justify-end gap-2 text-xs text-neutral-400">
          <button class="viewer-button py-1" :disabled="offset === 0" @click="offset -= 50">Previous readings</button
          ><span
            >{{ result.pointTotal ? offset + 1 : 0 }}–{{ Math.min(offset + 50, result.pointTotal) }} /
            {{ result.pointTotal }}</span
          ><button class="viewer-button py-1" :disabled="offset + 50 >= result.pointTotal" @click="offset += 50">
            Next readings
          </button>
        </div>
      </section>
      <section v-if="result.notifications" class="space-y-3">
        <div class="flex flex-wrap items-start justify-between gap-2">
          <h3 class="font-medium">City notifications</h3>
          <button class="text-xs text-pink-300 hover:underline" @click="openPoint(result.notifications.index)">
            Open notification evidence
          </button>
        </div>
        <p class="text-xs text-neutral-400">
          Last observed: {{ date(result.notifications) }} · {{ number(result.notifications.total) }} citywide ·
          {{ result.notifications.items.length }} detail rows{{ result.notifications.truncated ? ' · truncated' : '' }}.
        </p>
        <p class="text-xs text-neutral-500">
          The detail list follows the original tool's filters and row limit. Counts by type in the measures above remain
          citywide.
        </p>
        <div class="max-h-72 overflow-auto">
          <table class="w-full whitespace-nowrap text-left text-xs">
            <thead class="text-neutral-400">
              <tr>
                <th class="p-2">Notification</th>
                <th class="px-2">Priority</th>
                <th class="px-2">Place</th>
                <th class="px-2">Position (x, z)</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(item, index) in result.notifications.items" :key="index" class="border-t border-neutral-800">
                <td class="p-2">{{ item.type }}</td>
                <td class="px-2">{{ number(item.priority) }}</td>
                <td class="px-2">{{ item.target }}</td>
                <td class="px-2">{{ item.location }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
      <section v-if="result.media.length" class="space-y-3">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <h3 class="font-medium">Recorded city view</h3>
          <select
            v-model="imageIndex"
            aria-label="City image"
            class="max-w-full rounded border border-neutral-700 bg-neutral-900 px-2 py-1 text-xs"
          >
            <option v-for="item in result.media" :key="item.index" :value="item.index">
              {{ item.tool === 'map_image' ? 'Map' : 'Screenshot' }} ·
              {{ date(item.frame === undefined ? undefined : { frame: item.frame, gameTime: item.gameTime }) }}
            </option>
          </select>
        </div>
        <img
          v-if="imageUrl"
          :src="imageUrl"
          alt="Recorded city view at the selected city time"
          class="max-h-96 max-w-full rounded object-contain"
        />
        <p v-else class="text-xs text-neutral-500">Image unavailable or loading…</p>
        <button
          v-if="imageIndex !== undefined"
          class="text-xs text-pink-300 hover:underline"
          @click="openPoint(imageIndex)"
        >
          Open image evidence
        </button>
      </section>
      <section class="space-y-3">
        <h3 class="font-medium">City actions and before/after changes</h3>
        <p class="text-xs text-neutral-500">
          {{ result.eventTotal }} recorded actions up to this city time. Ledger changes and verdicts remain attached to
          their original calls.
        </p>
        <div v-for="event in result.events" :key="event.index" class="rounded border border-neutral-800 p-3">
          <div class="flex flex-wrap items-start justify-between gap-2">
            <button class="text-left text-pink-300 hover:underline" @click="openPoint(event.index)">
              {{ event.label }}</button
            ><span class="text-xs text-neutral-400"
              >{{ event.status }}{{ event.verdict ? ` · ${event.verdict}` : '' }}</span
            >
          </div>
          <p class="mt-1 text-xs text-neutral-500">
            {{ date(event.frame === undefined ? undefined : { frame: event.frame, gameTime: event.gameTime }) }}
          </p>
          <p v-for="change in event.changes" :key="change.field" class="mt-2 text-xs tabular-nums">
            {{ change.field }}: {{ number(change.before) }} → {{ number(change.after) }} ({{
              change.delta > 0 ? '+' : ''
            }}{{ number(change.delta) }})
          </p>
          <p v-if="event.note" class="mt-2 text-xs text-neutral-400">{{ event.note }}</p>
        </div>
        <div class="flex items-center justify-end gap-2 text-xs text-neutral-400">
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
      <p class="text-xs text-neutral-500">
        {{ result.observations }} successful city reads acquired · {{ result.untimed }} lack a simulation-time anchor
        and remain available in Evidence.
      </p>
      <details v-if="result.warnings.length" class="text-xs text-amber-300">
        <summary class="cursor-pointer">{{ result.warnings.length }} city data issues</summary>
        <p v-for="warning in result.warnings" :key="warning" class="mt-2">{{ warning }}</p>
      </details>
    </template>
  </div>
</template>
