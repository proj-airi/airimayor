<script setup lang="ts">
import type { EventLogState } from '@proj-airi/trace-kit/projections'
import type { TraceItemRef } from '@proj-airi/trace-kit/views'
import type { Frame, Selection, SessionSummary } from './viewer-session'
import type { LoadProgress } from './viewer-model'
import { createTraceViewReader } from '@proj-airi/trace-kit/views'
import { formatOffset as elapsed } from '@proj-airi/ui-timeline/headless'
import { TraceEventLog, TraceMetrics, TraceTimeline, ViewPresetMenu, useTracerView } from '@proj-airi/ui-trace'
import { useTheme } from '@proj-airi/ui'
import { proxy } from 'comlink'
import { sortedLastIndex } from 'es-toolkit/compat'
import { nextTick, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { VList } from 'virtua/vue'
import { Playback } from './playback'
import { ViewerClient } from './viewer-client'
import Evidence from './viewer-evidence.vue'
import Json from './viewer-json.vue'
import Analysis from './viewer-analysis.vue'
import Search from './viewer-search.vue'

type PickerWindow = Window & {
  showDirectoryPicker?: (options: { mode: 'read' }) => Promise<Parameters<ViewerClient['remote']['openDirectory']>[0]>
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
const view = ref<'timeline' | 'events' | 'metrics' | 'search'>('timeline')
const workspace = ref<'trace' | 'call'>('trace')
const log = shallowRef<EventLogState>(),
  logLoading = ref(false)
const folderInput = ref<HTMLInputElement>(),
  recordInput = ref<HTMLInputElement>(),
  timeline = ref<{ fit: () => void }>()
let activeClient: ViewerClient | undefined,
  player: Playback<Frame> | undefined,
  loadSerial = 0,
  requestSerial = 0,
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

function fitTimeline() {
  follow.value = false
  timeline.value?.fit()
}

watch([client, summary], ([client, summary]) => {
  player?.pause()
  player =
    client && summary
      ? new Playback(summary.playTimes, {
          read: (position) => client.remote.frame(position, frame.value?.selection, true),
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
  if (!current) return
  player?.pause()
  desiredSeek = { position, selection, serial: ++requestSerial }
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

async function select(item: TraceItemRef | undefined) {
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
      const position = item.kind === 'span' ? Math.max(frame.value?.position ?? 0, target.position) : target.position
      const span = item.kind === 'span' ? state.value.spans.get(item.key) : undefined
      tab.value = span?.track.endsWith('/llm') || target.selection?.kind === 'acp' ? 'replay' : 'detail'
      workspace.value = 'call'
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
  const position = summary.value.events.findIndex(event => event.index === index)
  if (position < 0) return
  workspace.value = 'call'
  tab.value = 'evidence'
  await seek(position)
}

async function open(
  load: (client: ViewerClient, progress: (update: LoadProgress) => Promise<void>) => Promise<SessionSummary>,
) {
  player?.pause()
  requestSerial++
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
    const initial = await next.remote.frame(result.events.length - 1, result.defaultSelection, false)
    if (serial !== loadSerial) return
    const loadedView = createTraceViewReader(result.clock, { seriesRetention: false })
    loadedView.reader.handleRecords(result.overview)
    overview.value = loadedView
    state.value = loadedView.timelineView()
    version.value++
    summary.value = result
    frame.value = initial
    client.value = next
    follow.value = false
    tab.value = 'replay'
    view.value = 'timeline'
    workspace.value = 'trace'
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
    player.play(frame.value.position === summary.value.events.length - 1 ? 0 : frame.value.position, speed.value)
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
    player.play(target.position, speed.value)
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
  if (playing.value && frame.value) player?.play(frame.value.position, speed.value)
}

watch(view, async (value) => {
  if (value !== 'events' || log.value || !client.value) return
  const current = client.value
  logLoading.value = true
  try {
    const result = await current.remote.eventLog()
    if (activeClient === current) log.value = result
  } catch (value) {
    if (activeClient === current) error.value = String(value)
  } finally {
    if (activeClient === current) logLoading.value = false
  }
})
</script>

<template>
  <header class="flex shrink-0 flex-wrap items-center gap-3 border-b border-neutral-800 px-4 py-3">
    <a class="text-sm font-medium" href="https://github.com/proj-airi/airimayor" target="_blank" rel="noreferrer"
      >AIRI Mayor</a
    >
    <span class="text-neutral-600" aria-hidden="true">/</span>
    <h1 class="text-sm font-medium text-pink-300">Trace</h1>
    <span
      v-if="summary"
      class="order-last w-full truncate text-neutral-400 md:order-none md:w-auto md:min-w-0 md:flex-1"
      :title="summary.name"
      >{{ summary.name }}</span
    >
    <span v-else class="ml-auto text-neutral-400">Offline viewer</span>
    <div v-if="summary || loading || error" class="ml-auto flex items-center gap-1">
      <button
        class="viewer-button"
        title="Open records.jsonl without attachments"
        aria-label="Open file"
        :disabled="loading"
        @click="recordInput?.click()"
      >
        <span class="viewer-icon i-mingcute:file-import-line" aria-hidden="true" /><span class="hidden sm:inline"
          >Open file</span
        >
      </button>
      <button
        class="viewer-button bg-pink-300 text-neutral-950 hover:bg-pink-200"
        :disabled="loading"
        @click="chooseFolder"
      >
        <span class="viewer-icon i-mingcute:folder-open-line" aria-hidden="true" />Open session
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
    <section v-if="!summary && !loading && !error" class="flex min-h-0 flex-1 overflow-y-auto px-6 text-center">
      <div class="m-auto flex max-w-full flex-col items-center pb-20 pt-11">
        <div class="w-[270px] max-w-full opacity-85" aria-hidden="true">
          <div class="flex justify-between pb-2.5 font-mono text-[9px] leading-none text-neutral-500">
            <span>0</span><span>5</span><span>10</span><span>15s</span>
          </div>
          <div class="relative h-[106px] border-t border-neutral-700">
            <div class="absolute inset-0 grid grid-cols-3">
              <span class="border-l border-neutral-800/40" />
              <span class="border-l border-neutral-800/40" />
              <span class="border-l border-neutral-800/40" />
            </div>
            <span
              class="absolute left-[4%] top-[15px] h-3 w-[90%] rounded border border-pink-300/45 bg-pink-300/15"
            />
            <span
              class="absolute left-[9%] top-[27px] h-[21px] w-[6%] rounded-bl-md border-b border-l border-pink-300/25"
            />
            <span class="absolute left-[16%] top-[41px] h-3 w-[33%] rounded bg-pink-300/50" />
            <span class="absolute left-[51%] top-[66px] h-3 w-[13%] rounded bg-emerald-200/40" />
            <span class="absolute left-[66%] top-[91px] h-3 w-[26%] rounded bg-pink-300/30" />
            <span class="absolute bottom-[-7px] left-[72%] top-[-3px] w-px bg-pink-300">
              <span class="absolute left-[-2px] top-[-2px] size-[5px] rounded-full bg-pink-300" />
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
          <button
            class="viewer-button rounded-lg bg-pink-300 px-[18px] py-3 text-neutral-950 hover:bg-pink-200"
            @click="chooseFolder"
          >
            <span class="viewer-icon i-mingcute:folder-open-line" aria-hidden="true" />Open session folder
          </button>
          <button
            class="viewer-button rounded-lg px-[18px] py-3"
            title="Open records.jsonl without attachments"
            @click="recordInput?.click()"
          >
            <span class="viewer-icon i-mingcute:file-import-line" aria-hidden="true" />Open file
          </button>
        </div>
        <p class="mt-5 font-mono text-xs text-neutral-500">records.jsonl + assets/</p>
      </div>
    </section>
    <p v-if="error" id="import-status" class="shrink-0 break-words py-3 text-red-400" role="alert">{{ error }}</p>
    <section v-if="loading" class="viewer-empty" role="status">
      <span
        class="i-mingcute:loading-line size-6 animate-spin text-pink-300 motion-reduce:animate-none"
        aria-hidden="true"
      />
      <h2 class="text-sm font-medium">{{ progress ? phases[progress.phase] : 'Opening session…' }}</h2>
      <progress
        id="import-progress"
        class="h-1 w-64 max-w-full accent-pink-300"
        :max="progress?.total || 1"
        :value="progress?.completed ?? 0"
      />
      <span v-if="progress" class="tabular-nums">{{ number(progress.completed) }} / {{ number(progress.total) }}</span>
      <button class="viewer-button" @click="cancelImport">Cancel</button>
    </section>
    <template v-if="summary && frame && client">
      <div class="relative flex shrink-0 flex-wrap items-center justify-between gap-3 py-3">
        <div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-neutral-400 tabular-nums">
          <span>{{ elapsed(summary.duration) }}</span
          ><span>{{ number(summary.models) }} model calls</span><span>{{ number(summary.tools) }} tool calls</span
          ><span>{{ number(summary.records) }} records</span>
        </div>
        <span
          v-if="!summary.issues.length"
          class="inline-flex items-center gap-2 text-neutral-400"
          title="Records and referenced files passed validation."
          ><span class="viewer-icon i-mingcute:check-circle-line text-emerald-400" aria-hidden="true" />Files
          checked</span
        >
        <details v-else class="text-amber-300">
          <summary class="cursor-pointer">{{ number(summary.issues.length) }} validation issues</summary>
          <div
            class="absolute right-0 top-full z-20 h-64 w-full overflow-hidden rounded-md border border-neutral-700 bg-neutral-900 shadow-lg md:w-96"
          >
            <VList :data="summary.issues" class="[scrollbar-gutter:stable]"
              ><template #default="{ item }"
                ><p class="break-words px-3 py-2">{{ item }}</p></template
              ></VList
            >
          </div>
        </details>
      </div>
      <div class="mb-2 flex shrink-0 gap-4 md:hidden" role="group" aria-label="Workspace">
        <button
          class="viewer-tab"
          :aria-pressed="workspace === 'trace'"
          :class="workspace === 'trace' ? 'border-pink-300 text-pink-300' : ''"
          @click="workspace = 'trace'"
        >
          Trace
        </button>
        <button
          class="viewer-tab"
          :aria-pressed="workspace === 'call'"
          :class="workspace === 'call' ? 'border-pink-300 text-pink-300' : ''"
          @click="workspace = 'call'"
        >
          Selected call
        </button>
      </div>
      <div class="grid min-h-0 flex-1 grid-cols-1 gap-3 md:grid-cols-[minmax(0,1fr)_minmax(22rem,0.6fr)]">
        <section
          class="min-h-0 min-w-0 flex-col overflow-hidden rounded-md border border-neutral-800"
          :class="workspace === 'trace' ? 'flex' : 'hidden md:flex'"
          aria-label="Session trace"
        >
          <div class="flex shrink-0 items-center justify-between gap-2 border-b border-neutral-800 px-3">
            <div class="flex gap-4" role="tablist" aria-label="Trace views">
              <button
                v-for="[value, label] in [
                  ['timeline', 'Timeline'],
                  ['events', 'Event log'],
                  ['metrics', 'Metrics'],
                  ['search', 'Search'],
                ] as const"
                :id="`view-tab-${value}`"
                :key="value"
                class="viewer-tab"
                role="tab"
                :aria-selected="view === value"
                aria-controls="trace-view"
                @click="view = value"
              >
                {{ label }}
              </button>
            </div>
            <div v-if="view === 'timeline'" class="flex items-center">
              <button
                class="viewer-icon-button"
                :class="follow ? 'text-pink-300' : ''"
                :aria-pressed="follow"
                aria-label="Follow playback"
                title="Follow playback"
                @click="follow = !follow"
              >
                <span class="viewer-icon i-mingcute:aiming-2-line" aria-hidden="true" />
              </button>
              <button class="viewer-icon-button" aria-label="Fit timeline" title="Fit timeline" @click="fitTimeline">
                <span class="viewer-icon i-mingcute:fullscreen-2-line" aria-hidden="true" />
              </button>
            </div>
          </div>
          <div
            id="trace-view"
            class="min-h-0 flex-1 overflow-hidden"
            role="tabpanel"
            :aria-labelledby="`view-tab-${view}`"
          >
            <div v-show="view === 'timeline'" class="flex h-full min-h-0 flex-col">
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
                <template #corner><ViewPresetMenu v-model="presetKey" :presets="presets" :locale="locale" /></template>
                <template #status>{{ playing ? 'Playing' : 'Paused' }}</template>
              </TraceTimeline>
            </div>
            <div v-if="view === 'events'" class="h-full min-h-0">
              <TraceEventLog
                v-if="log"
                class="!text-sm [&_.grid]:grid-cols-[4.5rem_6.5rem_minmax(0,1fr)_minmax(0,2fr)_minmax(0,0.6fr)] lt-md:[&_.grid]:grid-cols-[4.5rem_minmax(0,1fr)] lt-md:[&_.grid>span:nth-child(2)]:hidden lt-md:[&_.grid>span:nth-child(3)]:hidden lt-md:[&_.grid>span:nth-child(5)]:hidden"
                :log="log"
                :version="1"
                :origin="summary.origin"
                :late="0"
                @select="select"
              />
              <p v-else class="viewer-empty" role="status">{{ logLoading ? 'Loading events…' : 'No events.' }}</p>
            </div>
            <div v-if="view === 'metrics'" class="h-full overflow-y-auto">
              <TraceMetrics
                class="!text-sm"
                :state="state"
                :samples="overview.metricSamples()"
                :version="version"
                :locale="locale"
              />
              <Analysis :client="client" @select="select" @seek="seekTime(summary.origin + $event)" />
            </div>
            <Search v-if="view === 'search'" :client="client" @record="inspectRecord" />
          </div>
        </section>
        <section
          class="min-h-0 min-w-0 flex-col overflow-hidden rounded-md border border-neutral-800"
          :class="workspace === 'call' ? 'flex' : 'hidden md:flex'"
          aria-label="Selected call"
        >
          <div class="flex shrink-0 items-center justify-between gap-2 border-b border-neutral-800 px-3">
            <div class="flex gap-3" role="tablist" aria-label="Call views">
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
            <p v-if="frame.selection" class="mt-1 text-neutral-400">{{ statusLabels[frame.state] ?? frame.state }}</p>
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
                <pre v-if="frame.text" class="whitespace-pre-wrap break-words font-sans text-sm leading-relaxed">{{
                  frame.text
                }}</pre>
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
                  <button v-if="frame.selection && !frame.channel" class="viewer-button" @click="inspect('detail')">
                    View details
                  </button>
                </div>
              </div>
            </template>
            <Evidence v-else :client="client" :frame="frame" :clock="summary.clock" :mode="tab" @select="select" />
          </div>
        </section>
      </div>
      <section class="flex shrink-0 flex-wrap items-center gap-3 py-3 tabular-nums" aria-label="Replay controls">
        <div class="flex items-center">
          <button
            class="viewer-icon-button"
            aria-label="First event"
            title="First event"
            :disabled="frame.position === 0"
            @click="seek(0)"
          >
            <span class="viewer-icon i-mingcute:skip-previous-line" aria-hidden="true" />
          </button>
          <button
            class="viewer-icon-button"
            aria-label="Previous event"
            title="Previous event"
            :disabled="frame.position === 0"
            @click="seek(frame.position - 1)"
          >
            <span class="viewer-icon i-mingcute:left-line" aria-hidden="true" />
          </button>
          <button
            class="viewer-icon-button mx-1 bg-pink-300 text-neutral-950 hover:bg-pink-200"
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
            :disabled="frame.position === summary.events.length - 1"
            @click="seek(frame.position + 1)"
          >
            <span class="viewer-icon i-mingcute:right-line" aria-hidden="true" />
          </button>
          <button
            class="viewer-icon-button"
            aria-label="Last event"
            title="Last event"
            :disabled="frame.position === summary.events.length - 1"
            @click="seek(summary.events.length - 1)"
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
          :title="`Event ${number(frame.position + 1)} of ${number(summary.events.length)}`"
          >{{ number(frame.position + 1) }} / {{ number(summary.events.length) }}</span
        >
        <div class="flex w-full items-center gap-3 md:w-auto md:min-w-0 md:flex-1">
          <span>{{ elapsed(frame.time) }}</span
          ><input
            class="min-w-0 flex-1 cursor-pointer accent-pink-300"
            type="range"
            min="0"
            :max="summary.events.length - 1"
            :value="frame.position"
            aria-label="Replay event position"
            title="Replay in recorded order; gaps longer than 1.5 seconds are shortened."
            @input="seek(Number(($event.target as HTMLInputElement).value))"
          /><span class="text-neutral-400">{{ elapsed(summary.duration) }}</span>
        </div>
      </section>
    </template>
  </main>
</template>
