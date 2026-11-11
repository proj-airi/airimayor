<script setup lang="ts">
import type { EventLogState } from '@proj-airi/trace-kit/projections'
import type { TraceItemRef } from '@proj-airi/trace-kit/views'
import type { Frame, Selection, SessionSummary } from './viewer-session'
import type { LoadProgress } from './viewer-model'
import { createTraceViewReader } from '@proj-airi/trace-kit/views'
import { formatOffset as elapsed } from '@proj-airi/ui-timeline/headless'
import {
  TraceEventLog,
  TraceMetrics,
  TraceTimeline,
  ViewPresetMenu,
  useTracerView,
} from '@proj-airi/ui-trace'
import { Select, useTheme } from '@proj-airi/ui'
import { proxy } from 'comlink'
import { sortedLastIndex } from 'es-toolkit/compat'
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { VList } from 'virtua/vue'
import { Playback } from './playback'
import { ViewerClient } from './viewer-client'
import Evidence from './viewer-evidence.vue'
import Json from './viewer-json.vue'
import Analysis from './viewer-analysis.vue'
import Search from './viewer-search.vue'
import City from './viewer-city.vue'
import Workspace, { type ViewerPanel } from './viewer-workspace.vue'

type PickerWindow = Window & {
  showDirectoryPicker?: (options: {
    mode: 'read'
  }) => Promise<Parameters<ViewerClient['remote']['openDirectory']>[0]>
}
const { locale } = useI18n()
useTheme().isDark.value = true
const client = shallowRef<ViewerClient>(),
  summary = shallowRef<SessionSummary>(),
  frame = shallowRef<Frame>()
const selection = shallowRef<TraceItemRef>()
watch(
  () => frame.value?.item,
  (item) => {
    if (item?.kind !== selection.value?.kind || item?.key !== selection.value?.key) selection.value = item
  },
  { flush: 'sync' },
)
const overview = shallowRef(createTraceViewReader('mono:viewer', { seriesRetention: false }))
const state = shallowRef(overview.value.timelineView()),
  version = ref(0)
const { entities, details, hiddenTracks, toggleLane, setVisibility, presets, presetKey } = useTracerView({
  state,
  version,
  locale,
  indexes: () => overview.value.reader.indexes,
})
const loading = ref(false),
  progress = shallowRef<LoadProgress>(),
  error = ref(''),
  playing = ref(false),
  follow = ref(true),
  speed = ref(1)
const tab = ref<'replay' | 'detail' | 'evidence' | 'raw'>('replay')
const view = ref<ViewerPanel>('timeline')
const inspecting = ref(false)
const scopeLoading = ref(false)
const activeTurn = ref('')
const focusedTurn = computed(() => summary.value?.turns.find((turn) => turn.id === activeTurn.value))
const positions = computed(
  () => focusedTurn.value?.positions ?? summary.value?.events.map((_event, position) => position) ?? [0],
)
const first = computed(() => positions.value[0])
const last = computed(() => positions.value.at(-1)!)
const replayPosition = computed(() =>
  Math.max(0, sortedLastIndex(positions.value, frame.value?.position ?? 0) - 1),
)
const playTimes = computed(() => {
  const times = [0]
  for (let index = 1; index < positions.value.length; index++)
    times[index] =
      times[index - 1] +
      Math.min(
        1500,
        Math.max(
          0,
          summary.value!.events[positions.value[index]].time -
            summary.value!.events[positions.value[index - 1]].time,
        ),
      )
  return times
})
const rangeStart = computed(() => focusedTurn.value?.start ?? 0)
const rangeEnd = computed(() => focusedTurn.value?.end ?? summary.value?.duration ?? 0)
const cityInspect = ref(false)
const cityReturnPosition = ref<number>()
const cityReturnTurn = ref('')
const log = shallowRef<EventLogState>(),
  logLoading = ref(false)
const folderInput = ref<HTMLInputElement>(),
  recordInput = ref<HTMLInputElement>(),
  timeline = ref<{ fit: () => void }>()
let activeClient: ViewerClient | undefined,
  player: Playback<Frame> | undefined,
  loadSerial = 0,
  requestSerial = 0,
  scopeSerial = 0,
  seeking = false
let desiredSeek: { position: number; selection?: Selection; serial: number } | undefined
const phases = {
  read: 'Reading records',
  records: 'Validating records',
  assets: 'Validating attachments',
  timeline: 'Building timeline',
}
const number = (value: number) => value.toLocaleString()
const traceNow = () => (summary.value && frame.value ? summary.value.origin + frame.value.time : undefined)
const statusLabels: Record<string, string> = { ok: 'Completed', error: 'Failed', cancelled: 'Cancelled' }
const scopeOptions = computed(() => [
  { value: 0, label: 'Whole session' },
  ...(summary.value?.turns.map((turn, index) => ({
    value: turn.id,
    label: `Turn ${index + 1}`,
    description: `${statusLabels[turn.status] ?? turn.status} · ${elapsed(turn.end - turn.start)}`,
  })) ?? []),
])
const scopeValue = computed<string | number>({
  get: () => activeTurn.value || 0,
  set: (value: string | number) => {
    activeTurn.value = typeof value === 'string' ? value : ''
    void changeTurn()
  },
})

function fitTimeline() {
  follow.value = false
  timeline.value?.fit()
}

watch([client, summary, activeTurn], ([client, summary]) => {
  player?.pause()
  player =
    client && summary
      ? new Playback(playTimes.value, {
          read: (position) => client.remote.frame(positions.value[position], frame.value?.selection, true),
          commit: (value) => {
            frame.value = value
          },
          state: (value) => {
            playing.value = value
          },
          error: (value) => {
            error.value = String(value)
          },
        })
      : undefined
})
onBeforeUnmount(() => {
  player?.pause()
  activeClient?.dispose()
})

async function seek(position: number, selection?: Selection) {
  const current = client.value
  if (!current || scopeLoading.value) return
  player?.pause()
  desiredSeek = {
    position:
      positions.value[Math.max(0, sortedLastIndex(positions.value, Math.min(last.value, position)) - 1)],
    selection,
    serial: ++requestSerial,
  }
  if (seeking) return
  seeking = true
  try {
    while (desiredSeek && activeClient === current) {
      const desired = desiredSeek
      desiredSeek = undefined
      const next = await current.remote.frame(
        desired.position,
        desired.selection ?? frame.value?.selection,
        !desired.selection,
      )
      if (desired.serial === requestSerial && activeClient === current) frame.value = next
    }
  } catch (value) {
    if (activeClient === current) error.value = String(value)
  } finally {
    seeking = false
  }
}

function seekPosition(position: number) {
  return seek(positions.value[Math.max(0, Math.min(positions.value.length - 1, position))])
}

async function select(item: TraceItemRef | undefined) {
  if (scopeLoading.value) return
  if (!item) {
    player?.pause()
    requestSerial++
    desiredSeek = undefined
    if (frame.value)
      frame.value = {
        ...frame.value,
        selection: undefined,
        item: undefined,
        heading: 'Select a call or event',
        state: 'No selection',
        channel: false,
        text: '',
        reasoning: '',
        chunks: 0,
        completedOutput: undefined,
      }
    return
  }
  if (!client.value) return
  player?.pause()
  const serial = ++requestSerial,
    current = client.value
  try {
    const target = await current.remote.locate(item)
    if (serial === requestSerial && activeClient === current) {
      if (!positions.value.includes(target.position)) {
        activeTurn.value = ''
        await changeTurn()
      }
      const position =
        item.kind === 'span' ? Math.max(frame.value?.position ?? 0, target.position) : target.position
      const span = item.kind === 'span' ? state.value.spans.get(item.key) : undefined
      tab.value = span?.track.endsWith('/llm') || target.selection?.kind === 'acp' ? 'replay' : 'detail'
      inspecting.value = true
      await seek(position, target.selection)
    }
  } catch (value) {
    if (serial === requestSerial) error.value = String(value)
  }
}

function seekTime(time: number | undefined) {
  if (time === undefined || !summary.value) return
  const position = Math.max(0, sortedLastIndex(summary.value.seekTimes, time - summary.value.origin) - 1)
  void seek(summary.value.seekPositions[position])
}

async function inspectRecord(index: number) {
  if (!summary.value) return
  const position = summary.value.events.findIndex((event) => event.index === index)
  if (position < 0) return
  if (view.value === 'city') {
    if (!cityInspect.value) {
      cityReturnPosition.value = frame.value?.position
      cityReturnTurn.value = activeTurn.value
    }
  }
  if (!positions.value.includes(position)) {
    activeTurn.value = ''
    await changeTurn()
  }
  if (view.value === 'city') cityInspect.value = true
  inspecting.value = true
  tab.value = 'evidence'
  await seek(position)
}

async function closeCityEvidence() {
  cityInspect.value = false
  inspecting.value = false
  const position = cityReturnPosition.value
  cityReturnPosition.value = undefined
  if (activeTurn.value !== cityReturnTurn.value) {
    activeTurn.value = cityReturnTurn.value
    await changeTurn()
  }
  if (position !== undefined) await seek(position)
}

async function open(
  load: (client: ViewerClient, progress: (update: LoadProgress) => Promise<void>) => Promise<SessionSummary>,
) {
  player?.pause()
  requestSerial++
  scopeSerial++
  scopeLoading.value = false
  desiredSeek = undefined
  seeking = false
  const serial = ++loadSerial
  activeClient?.dispose()
  activeClient = undefined
  client.value = undefined
  summary.value = undefined
  frame.value = undefined
  log.value = undefined
  progress.value = undefined
  error.value = ''
  loading.value = true
  try {
    const next = new ViewerClient((value) => {
      if (serial === loadSerial) {
        error.value = value
        loading.value = false
      }
    })
    activeClient = next
    const result = await load(
      next,
      proxy(async (update: LoadProgress) => {
        if (serial === loadSerial) progress.value = update
      }),
    )
    if (serial !== loadSerial) return
    const turn = result.turns.at(-1)
    const initial = await next.remote.frame(
      turn?.positions.at(-1) ?? result.events.length - 1,
      undefined,
      false,
    )
    if (serial !== loadSerial) return
    const loadedView = createTraceViewReader(result.clock, { seriesRetention: false })
    const records = await next.remote.overview(turn?.id)
    if (serial !== loadSerial) return
    loadedView.reader.handleRecords(records)
    overview.value = loadedView
    state.value = loadedView.timelineView()
    version.value++
    summary.value = result
    activeTurn.value = turn?.id ?? ''
    frame.value = initial
    client.value = next
    follow.value = false
    tab.value = 'replay'
    view.value = 'timeline'
    inspecting.value = false
    cityInspect.value = false
    cityReturnPosition.value = undefined
    loading.value = false
    await nextTick()
    timeline.value?.fit()
  } catch (value) {
    if (serial === loadSerial) {
      error.value = String(value)
      loading.value = false
      activeClient?.dispose()
      activeClient = undefined
    }
  }
}

async function chooseFolder() {
  const picker = (window as PickerWindow).showDirectoryPicker
  if (!picker) {
    folderInput.value?.click()
    return
  }
  try {
    const directory = await picker.call(window, { mode: 'read' })
    void open((client, progress) => client.remote.openDirectory(directory, progress))
  } catch (value) {
    if (!(value instanceof DOMException && value.name === 'AbortError')) error.value = String(value)
  }
}

function cancelImport() {
  loadSerial++
  activeClient?.dispose()
  activeClient = undefined
  loading.value = false
}

function filesSelected(event: Event) {
  const input = event.target as HTMLInputElement
  if (!input.files?.length) return
  const files = Array.from(input.files, (file) => ({ path: file.webkitRelativePath || file.name, file }))
  input.value = ''
  void open((client, progress) => client.remote.openFiles(files, progress))
}

function togglePlay() {
  if (!player || !summary.value || !frame.value) return
  requestSerial++
  desiredSeek = undefined
  if (playing.value) player.pause()
  else {
    tab.value = 'replay'
    player.play(frame.value.position >= last.value ? 0 : replayPosition.value, speed.value)
  }
}
async function replaySelection() {
  const item = selection.value,
    current = client.value
  if (!item || !current || !player) return
  player.pause()
  desiredSeek = undefined
  const serial = ++requestSerial
  try {
    const target = await current.remote.locate(item)
    if (serial !== requestSerial || activeClient !== current) return
    tab.value = 'replay'
    requestSerial++
    player.play(Math.max(0, sortedLastIndex(positions.value, target.position) - 1), speed.value)
  } catch (value) {
    if (serial === requestSerial) error.value = String(value)
  }
}

function inspect(value: typeof tab.value) {
  player?.pause()
  requestSerial++
  desiredSeek = undefined
  tab.value = value
}
function changeSpeed() {
  if (playing.value && frame.value) player?.play(replayPosition.value, speed.value)
}

async function changeTurn() {
  const current = client.value
  if (!current || !summary.value) return
  player?.pause()
  desiredSeek = undefined
  inspecting.value = false
  cityInspect.value = false
  log.value = undefined
  requestSerial++
  const serial = ++scopeSerial
  scopeLoading.value = true
  try {
    const records = await current.remote.overview(activeTurn.value || undefined)
    const next = await current.remote.frame(last.value, undefined, false)
    if (serial !== scopeSerial || activeClient !== current) return
    const reader = createTraceViewReader(summary.value.clock, { seriesRetention: false })
    reader.reader.handleRecords(records)
    overview.value = reader
    state.value = reader.timelineView()
    version.value++
    frame.value = next
    follow.value = false
    await nextTick()
    timeline.value?.fit()
  } catch (value) {
    if (serial === scopeSerial) error.value = String(value)
  } finally {
    if (serial === scopeSerial) scopeLoading.value = false
  }
}

watch(view, (value) => {
  if (value === 'city' && !cityInspect.value) inspecting.value = false
})
watch([view, activeTurn], async ([value, turn]) => {
  if (value !== 'events' || log.value || !client.value) return
  const current = client.value
  logLoading.value = true
  try {
    const result = await current.remote.eventLog(turn || undefined)
    if (activeClient === current && turn === activeTurn.value) log.value = result
  } catch (value) {
    if (activeClient === current) error.value = String(value)
  } finally {
    if (activeClient === current) logLoading.value = false
  }
})
</script>

<template>
  <header class="flex shrink-0 flex-wrap items-center gap-3 border-b border-neutral-800 px-4 py-3">
    <a
      class="text-sm font-medium"
      href="https://github.com/proj-airi/airimayor"
      target="_blank"
      rel="noreferrer"
    >
      AIRI Mayor
    </a>
    <span class="text-neutral-600" aria-hidden="true">/</span>
    <h1 class="text-sm font-medium text-primary-300">Trace</h1>
    <span
      v-if="summary"
      class="order-last w-full truncate text-neutral-400 md:order-none md:w-auto md:min-w-0 md:flex-1"
      :title="summary.name"
    >
      {{ summary.name }}
    </span>
    <span v-else class="ml-auto text-neutral-400">Offline viewer</span>
    <div v-if="summary || loading || error" class="ml-auto flex items-center gap-1">
      <button
        class="viewer-button"
        title="Open records.jsonl without attachments"
        aria-label="Open file"
        :disabled="loading"
        @click="recordInput?.click()"
      >
        <span class="viewer-icon i-mingcute:file-import-line" aria-hidden="true" />
        <span class="hidden sm:inline">Open file</span>
      </button>
      <button class="viewer-button viewer-accent-button" :disabled="loading" @click="chooseFolder">
        <span class="viewer-icon i-mingcute:folder-open-line" aria-hidden="true" />
        Open session
      </button>
    </div>
    <input
      ref="folderInput"
      type="file"
      webkitdirectory
      multiple
      hidden
      aria-label="Select session record folder"
      @change="filesSelected"
    />
    <input
      ref="recordInput"
      type="file"
      accept=".jsonl"
      hidden
      aria-label="Select records.jsonl"
      @change="filesSelected"
    />
  </header>
  <main class="flex min-h-0 flex-1 flex-col px-3 [&_.font-semibold]:font-medium">
    <section
      v-if="!summary && !loading && !error"
      class="flex min-h-0 flex-1 overflow-y-auto px-6 text-center"
    >
      <div class="m-auto flex max-w-full flex-col items-center pb-20 pt-11">
        <div class="w-[270px] max-w-full opacity-85" aria-hidden="true">
          <div class="flex justify-between pb-2.5 font-mono text-[9px] leading-none text-neutral-500">
            <span>0</span>
            <span>5</span>
            <span>10</span>
            <span>15s</span>
          </div>
          <div class="relative h-[106px] border-t border-neutral-700">
            <div class="absolute inset-0 grid grid-cols-3">
              <span class="border-l border-neutral-800/40" />
              <span class="border-l border-neutral-800/40" />
              <span class="border-l border-neutral-800/40" />
            </div>
            <span
              class="absolute left-[4%] top-[15px] h-3 w-[90%] rounded border border-primary-300/45 bg-primary-300/15"
            />
            <span
              class="absolute left-[9%] top-[27px] h-[21px] w-[6%] rounded-bl-md border-b border-l border-primary-300/25"
            />
            <span class="absolute left-[16%] top-[41px] h-3 w-[33%] rounded bg-primary-300/50" />
            <span class="absolute left-[51%] top-[66px] h-3 w-[13%] rounded bg-emerald-200/40" />
            <span class="absolute left-[66%] top-[91px] h-3 w-[26%] rounded bg-primary-300/30" />
            <span class="absolute bottom-[-7px] left-[72%] top-[-3px] w-px bg-primary-300">
              <span class="absolute left-[-2px] top-[-2px] size-[5px] rounded-full bg-primary-300" />
            </span>
          </div>
        </div>
        <h2
          class="mt-8 font-['Comfortaa_Variable'] text-6xl font-normal leading-[1.15] tracking-[-0.06em] text-neutral-200 sm:text-7xl"
        >
          Trace
        </h2>
        <p class="mt-3.5 text-sm text-neutral-400">Open a session record.</p>
        <div class="mt-8 flex flex-wrap justify-center gap-2.5">
          <button class="viewer-button viewer-accent-button rounded-lg px-[18px] py-3" @click="chooseFolder">
            <span class="viewer-icon i-mingcute:folder-open-line" aria-hidden="true" />
            Open session folder
          </button>
          <button
            class="viewer-button rounded-lg px-[18px] py-3"
            title="Open records.jsonl without attachments"
            @click="recordInput?.click()"
          >
            <span class="viewer-icon i-mingcute:file-import-line" aria-hidden="true" />
            Open file
          </button>
        </div>
        <p class="mt-5 font-mono text-xs text-neutral-500">records.jsonl + assets/</p>
      </div>
    </section>
    <p v-if="error" id="import-status" class="shrink-0 break-words py-3 text-red-400" role="alert">
      {{ error }}
    </p>
    <section v-if="loading" class="viewer-empty" role="status">
      <span
        class="i-mingcute:loading-line size-6 animate-spin text-primary-300 motion-reduce:animate-none"
        aria-hidden="true"
      />
      <h2 class="text-sm font-medium">{{ progress ? phases[progress.phase] : 'Opening session…' }}</h2>
      <progress
        id="import-progress"
        class="h-1 w-64 max-w-full accent-primary-300"
        :max="progress?.total || 1"
        :value="progress?.completed ?? 0"
      />
      <span v-if="progress" class="tabular-nums">
        {{ number(progress.completed) }} / {{ number(progress.total) }}
      </span>
      <button class="viewer-button" @click="cancelImport">Cancel</button>
    </section>
    <template v-if="summary && frame && client">
      <div class="flex shrink-0 flex-wrap items-center gap-3 px-2 py-2">
        <label class="flex min-w-0 flex-1 items-center gap-2 text-neutral-400">
          <span class="shrink-0" aria-hidden="true">Focus</span>
          <span class="sr-only">Recording scope</span>
          <Select
            v-model="scopeValue"
            :options="scopeOptions"
            :content-min-width="0"
            :disabled="scopeLoading"
            class="min-w-0 max-w-xl flex-1"
          >
            <template #value="{ option }">
              <span class="block truncate">{{ option?.label }}</span>
            </template>
          </Select>
        </label>
        <span class="text-xs text-neutral-400">
          {{ focusedTurn?.models ?? summary.models }} model calls ·
          {{ focusedTurn?.tools ?? summary.tools }} tools
        </span>
        <span
          v-if="!summary.issues.length"
          class="text-xs text-emerald-300"
          title="Records and referenced files passed validation."
        >
          Files checked
        </span>
        <details v-else class="relative text-xs text-amber-300">
          <summary class="cursor-pointer">{{ number(summary.issues.length) }} file issues</summary>
          <div
            class="absolute right-0 top-full z-20 h-56 w-72 overflow-hidden rounded border border-neutral-700 bg-neutral-900"
          >
            <VList :data="summary.issues">
              <template #default="{ item }">
                <p class="break-words p-2">{{ item }}</p>
              </template>
            </VList>
          </div>
        </details>
      </div>
      <div v-if="focusedTurn" class="flex shrink-0 items-start gap-3 px-2 pb-2 text-xs text-neutral-400">
        <p class="line-clamp-2 min-w-0 flex-1" :title="focusedTurn.label">{{ focusedTurn.label }}</p>
        <span class="shrink-0">
          {{ statusLabels[focusedTurn.status] ?? focusedTurn.status }} ·
          {{ elapsed(focusedTurn.end - focusedTurn.start) }}
        </span>
      </div>
      <div class="relative min-h-0 flex-1">
        <p
          v-if="scopeLoading"
          class="absolute inset-0 z-30 flex items-center justify-center bg-neutral-950/70"
          role="status"
        >
          Changing turn…
        </p>
        <Workspace v-model="view" :inspecting="inspecting" :inert="scopeLoading">
          <template #timeline>
            <section class="flex h-full min-h-0 flex-col" aria-label="Session trace">
              <div
                class="flex shrink-0 items-center justify-between border-b border-neutral-800 px-2 py-1 text-xs text-neutral-400"
              >
                <span>{{ focusedTurn ? 'Selected turn' : 'Whole session' }} · elapsed recording time</span>
                <div class="flex items-center">
                  <button
                    class="viewer-icon-button"
                    :aria-pressed="follow"
                    aria-label="Follow playback"
                    title="Follow playback"
                    @click="follow = !follow"
                  >
                    <span class="viewer-icon i-mingcute:aiming-2-line" aria-hidden="true" />
                  </button>
                  <button
                    class="viewer-icon-button"
                    aria-label="Fit timeline"
                    title="Fit timeline"
                    @click="fitTimeline"
                  >
                    <span class="viewer-icon i-mingcute:fullscreen-2-line" aria-hidden="true" />
                  </button>
                </div>
              </div>
              <TraceTimeline
                ref="timeline"
                class="!text-sm"
                :state="state"
                :indexes="overview.reader.indexes"
                :version="version"
                :lanes="entities.lanes"
                :connectors="entities.connectors"
                :details="details"
                :selection="selection"
                :cursor="summary.origin + frame.time"
                :follow="follow"
                :now="traceNow"
                :hidden="hiddenTracks"
                @select="select"
                @cursor="seekTime"
                @unfollow="follow = false"
                @toggle="toggleLane"
                @visibility="setVisibility"
              >
                <template #corner>
                  <ViewPresetMenu v-model="presetKey" :presets="presets" :locale="locale" />
                </template>
                <template #status>{{ playing ? 'Playing' : 'Paused' }}</template>
              </TraceTimeline>
            </section>
          </template>
          <template #events>
            <div class="h-full min-h-0">
              <TraceEventLog
                v-if="log"
                class="!text-sm [&_.grid]:grid-cols-[4.5rem_6.5rem_minmax(0,1fr)_minmax(0,2fr)_minmax(0,0.6fr)] lt-md:[&_.grid]:grid-cols-[4.5rem_minmax(0,1fr)] lt-md:[&_.grid>span:nth-child(2)]:hidden lt-md:[&_.grid>span:nth-child(3)]:hidden lt-md:[&_.grid>span:nth-child(5)]:hidden"
                :log="log"
                :version="1"
                :origin="summary.origin"
                :late="0"
                @select="select"
              />
              <p v-else class="viewer-empty" role="status">
                {{ logLoading ? 'Loading events…' : 'No events.' }}
              </p>
            </div>
          </template>
          <template #metrics>
            <Analysis
              :client="client"
              :turn="activeTurn || undefined"
              @select="select"
              @seek="seekTime(summary.origin + $event)"
            >
              <details class="rounded border border-neutral-800 text-xs">
                <summary class="cursor-pointer p-3 text-neutral-400">
                  Individual metric distributions · p50 / p95
                </summary>
                <TraceMetrics
                  :state="state"
                  :samples="overview.metricSamples()"
                  :version="version"
                  :locale="locale"
                />
              </details>
            </Analysis>
          </template>
          <template #search>
            <Search :client="client" :turn="activeTurn || undefined" @record="inspectRecord" />
          </template>
          <template #city>
            <KeepAlive>
              <City
                v-if="view === 'city'"
                :key="summary.name"
                :client="client"
                :position="frame.position"
                @record="inspectRecord"
              />
            </KeepAlive>
          </template>
          <template #inspector>
            <section class="flex h-full min-h-0 flex-col overflow-hidden">
              <div class="flex shrink-0 items-center justify-between gap-2 border-b border-neutral-800 px-3">
                <div class="flex min-w-0 gap-3 overflow-x-auto" role="tablist" aria-label="Call views">
                  <button
                    v-for="[value, label] in [
                      ['replay', 'Output'],
                      ['detail', 'Details'],
                      ['evidence', 'Evidence'],
                      ['raw', 'Records'],
                    ] as const"
                    :id="`inspector-tab-${value}`"
                    :key="value"
                    class="viewer-tab"
                    role="tab"
                    :aria-selected="tab === value"
                    aria-controls="call-view"
                    @click="inspect(value)"
                  >
                    {{ label }}
                  </button>
                </div>
                <button
                  class="viewer-icon-button"
                  aria-label="Close selected call"
                  title="Close selected call"
                  @click="cityInspect ? closeCityEvidence() : (inspecting = false)"
                >
                  <span class="viewer-icon i-mingcute:close-line" aria-hidden="true" />
                </button>
                <button
                  class="viewer-icon-button"
                  :disabled="!selection"
                  aria-label="Play from selection"
                  title="Play from selection"
                  @click="replaySelection"
                >
                  <span class="viewer-icon i-mingcute:back-2-line" aria-hidden="true" />
                </button>
              </div>
              <div v-if="tab === 'replay'" class="shrink-0 border-b border-neutral-800 p-3">
                <h2 class="text-sm font-medium">{{ frame.selection ? frame.heading : 'No selection' }}</h2>
                <p v-if="frame.selection" class="mt-1 text-neutral-400">
                  {{ statusLabels[frame.state] ?? frame.state }}
                </p>
              </div>
              <div
                id="call-view"
                class="flex min-h-0 flex-1 flex-col overflow-hidden"
                role="tabpanel"
                :aria-labelledby="`inspector-tab-${tab}`"
              >
                <template v-if="tab === 'replay'">
                  <Json v-if="frame.completedOutput !== undefined" :data="frame.completedOutput" />
                  <div v-else class="min-h-0 flex-1 overflow-y-auto p-3 [scrollbar-gutter:stable]">
                    <details v-if="frame.reasoning" class="mb-3 text-neutral-400">
                      <summary class="cursor-pointer">Reasoning</summary>
                      <pre class="mt-3 whitespace-pre-wrap break-words font-sans text-sm leading-relaxed">{{
                        frame.reasoning
                      }}</pre>
                    </details>
                    <pre
                      v-if="frame.text"
                      class="whitespace-pre-wrap break-words font-sans text-sm leading-relaxed"
                      >{{ frame.text }}</pre
                    >
                    <div v-if="!frame.text && !frame.reasoning" class="viewer-empty">
                      <p>
                        {{
                          !frame.selection
                            ? 'Select a call in the timeline.'
                            : frame.channel
                              ? 'No output at this point.'
                              : 'No text output for this call.'
                        }}
                      </p>
                      <button
                        v-if="frame.selection && !frame.channel"
                        class="viewer-button"
                        @click="inspect('detail')"
                      >
                        View details
                      </button>
                    </div>
                  </div>
                </template>
                <Evidence
                  v-else
                  :client="client"
                  :frame="frame"
                  :clock="summary.clock"
                  :mode="tab"
                  @select="select"
                />
              </div>
            </section>
          </template>
        </Workspace>
      </div>
      <section
        class="flex shrink-0 flex-wrap items-center gap-3 py-3 tabular-nums"
        aria-label="Replay controls"
        :inert="scopeLoading"
      >
        <div class="flex items-center">
          <button
            class="viewer-icon-button"
            aria-label="First event"
            title="First event"
            :disabled="frame.position <= first"
            @click="seekPosition(0)"
          >
            <span class="viewer-icon i-mingcute:skip-previous-line" aria-hidden="true" />
          </button>
          <button
            class="viewer-icon-button"
            aria-label="Previous event"
            title="Previous event"
            :disabled="frame.position <= first"
            @click="seekPosition(replayPosition - 1)"
          >
            <span class="viewer-icon i-mingcute:left-line" aria-hidden="true" />
          </button>
          <button
            class="viewer-icon-button viewer-accent-button mx-1"
            :aria-label="playing ? 'Pause' : 'Play'"
            :title="playing ? 'Pause' : 'Play'"
            @click="togglePlay"
          >
            <span
              class="viewer-icon"
              :class="playing ? 'i-mingcute:pause-fill' : 'i-mingcute:play-fill'"
              aria-hidden="true"
            />
          </button>
          <button
            class="viewer-icon-button"
            aria-label="Next event"
            title="Next event"
            :disabled="frame.position >= last"
            @click="seekPosition(replayPosition + 1)"
          >
            <span class="viewer-icon i-mingcute:right-line" aria-hidden="true" />
          </button>
          <button
            class="viewer-icon-button"
            aria-label="Last event"
            title="Last event"
            :disabled="frame.position >= last"
            @click="seekPosition(positions.length - 1)"
          >
            <span class="viewer-icon i-mingcute:skip-forward-line" aria-hidden="true" />
          </button>
        </div>
        <select
          v-model="speed"
          class="rounded-md border border-neutral-700 bg-neutral-900 px-2 py-1"
          aria-label="Playback speed"
          @change="changeSpeed"
        >
          <option v-for="value in [0.5, 1, 2, 4, 8]" :key="value" :value="value">{{ value }}×</option>
        </select>
        <span
          class="ml-auto text-neutral-400 md:order-last"
          :title="`Event ${number(replayPosition + 1)} of ${number(positions.length)} in this view`"
        >
          {{ number(replayPosition + 1) }} / {{ number(positions.length) }}
        </span>
        <div class="flex w-full items-center gap-3 md:w-auto md:min-w-0 md:flex-1">
          <span>{{ elapsed(frame.time - rangeStart) }}</span>
          <input
            class="min-w-0 flex-1 cursor-pointer accent-primary-300"
            type="range"
            min="0"
            :max="positions.length - 1"
            :value="replayPosition"
            aria-label="Replay event position"
            title="Replay in recorded order; gaps longer than 1.5 seconds are shortened."
            @input="seekPosition(Number(($event.target as HTMLInputElement).value))"
          />
          <span class="text-neutral-400">{{ elapsed(rangeEnd - rangeStart) }}</span>
        </div>
      </section>
    </template>
  </main>
</template>
