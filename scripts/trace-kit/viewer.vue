<script setup lang="ts">
import type { EventLogState } from '@proj-airi/trace-kit/projections'
import type { TraceItemRef } from '@proj-airi/trace-kit/views'
import type { Frame, Selection, SessionSummary } from './viewer-session'
import type { LoadProgress } from './viewer-model'
import { createTraceViewReader } from '@proj-airi/trace-kit/views'
import { TraceEventLog, TraceMetrics, TraceTimeline, ViewPresetMenu, useTracerView } from '@proj-airi/ui-trace'
import { useTheme } from '@proj-airi/ui'
import { proxy } from 'comlink'
import { sortedLastIndex } from 'es-toolkit/compat'
import { onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Playback } from './playback'
import { ViewerClient } from './viewer-client'
import Evidence from './viewer-evidence.vue'
import Json from './viewer-json.vue'

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
const tab = ref<'replay' | 'detail' | 'raw' | 'events' | 'metrics'>('replay')
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
const elapsed = (value: number) => `${(value / 1000).toFixed(3)}s`
const traceNow = () => (summary.value && frame.value ? summary.value.origin + frame.value.time : undefined)

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
    const view = createTraceViewReader(result.clock, { seriesRetention: false })
    view.reader.handleRecords(result.overview)
    overview.value = view
    state.value = view.timelineView()
    version.value++
    summary.value = result
    frame.value = initial
    client.value = next
    follow.value = true
    tab.value = 'replay'
    loading.value = false
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

watch(tab, async (value) => {
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
  <header class="app-header">
    <div class="brand">
      <span class="brand-mark">a</span>
      <div><strong>AIRI MAYOR</strong><span>SESSION REPLAY</span></div>
    </div>
    <div class="header-title">
      <span class="eyebrow">LOCAL RECORDING · AIRI TRACE UI</span>
      <h1>{{ summary?.name ?? 'Follow the mayor’s work.' }}</h1>
    </div>
    <div class="import-actions">
      <button class="button primary" :disabled="loading" @click="chooseFolder">Open record folder</button
      ><button class="button subtle" :disabled="loading" @click="recordInput?.click()">Open records.jsonl</button>
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
  <main>
    <section v-if="!summary && !loading && !error" class="empty-state">
      <span class="eyebrow">EVERY STEP, IN CONTEXT</span>
      <h2>See how a session unfolded.</h2>
      <p>Inspect the AIRI trace timeline, calls and causal links.<br />Replay the mayor’s output in recorded order.</p>
      <button class="button primary" @click="chooseFolder">Choose a session folder ↗</button>
      <p class="local-note">
        Select the folder containing <code>records.jsonl</code> and <code>assets</code>.<br />Everything stays in this
        browser. No server or network is used.
      </p>
    </section>
    <p v-if="error" id="import-status" class="error" role="alert">{{ error }}</p>
    <section v-if="loading" class="import-loading" role="status">
      <p>
        {{
          progress
            ? `${phases[progress.phase]} · ${number(progress.completed)} / ${number(progress.total)}`
            : 'Opening recording…'
        }}
      </p>
      <progress id="import-progress" :max="progress?.total || 1" :value="progress?.completed ?? 0" /><button
        class="button subtle"
        @click="cancelImport"
      >
        Cancel import
      </button>
    </section>
    <template v-if="summary && frame && client">
      <div class="session-summary">
        <div class="stats">
          <span
            ><b>{{ number(summary.records) }}</b> records</span
          ><span
            ><b>{{ number(summary.models) }}</b> model requests</span
          ><span
            ><b>{{ number(summary.tools) }}</b> city tools</span
          ><span
            ><b>{{ elapsed(summary.duration) }}</b> recorded</span
          >
        </div>
        <span class="badge" :class="{ warning: summary.issues.length }">{{
          summary.issues.length ? `${number(summary.issues.length)} evidence issues` : 'Evidence validated'
        }}</span>
      </div>
      <details v-if="summary.issues.length" class="issues">
        <summary>{{ number(summary.issues.length) }} validation issues</summary>
        <Json :data="summary.issues" />
      </details>
      <section class="playback panel" aria-label="Replay controls">
        <div class="transport">
          <div class="transport-buttons">
            <button class="icon-button" aria-label="First event" @click="seek(0)">|◀</button
            ><button class="icon-button" aria-label="Previous event" @click="seek(frame.position - 1)">‹</button
            ><button class="button play-button" @click="togglePlay">{{ playing ? 'Ⅱ Pause' : '▶ Play' }}</button
            ><button class="icon-button" aria-label="Next event" @click="seek(frame.position + 1)">›</button
            ><button class="icon-button" aria-label="Last event" @click="seek(summary.events.length - 1)">▶|</button>
          </div>
          <span class="cursor-label"
            >{{ number(frame.position + 1) }} / {{ number(summary.events.length) }} · {{ elapsed(frame.time) }}</span
          >
          <div class="transport-options">
            <label
              >Speed
              <select v-model="speed" @change="changeSpeed">
                <option v-for="value in [0.5, 1, 2, 4, 8]" :key="value" :value="value">{{ value }}×</option>
              </select></label
            ><label class="follow"><input v-model="follow" type="checkbox" /> Follow cursor</label
            ><button class="button subtle" @click="timeline?.fit()">Fit timeline</button>
          </div>
        </div>
        <input
          type="range"
          min="0"
          :max="summary.events.length - 1"
          :value="frame.position"
          aria-label="Replay event position"
          @input="seek(Number(($event.target as HTMLInputElement).value))"
        />
        <div class="range-labels">
          <span>0.000s</span><span>Recorded order · long gaps capped at 1.5 seconds during playback</span
          ><span>{{ elapsed(summary.duration) }}</span>
        </div>
      </section>
      <section class="panel airi-timeline" aria-label="AIRI execution timeline">
        <TraceTimeline
          ref="timeline"
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
          <template #status>Full recording overview</template>
        </TraceTimeline>
      </section>
      <p class="timeline-footnote">
        AIRI entity lanes and track rows. Select a call to inspect it, or click the time axis to seek. Replay from
        selection starts at that call or event. Evidence follows the replay cursor.
      </p>
      <section class="inspector panel">
        <div class="inspector-header">
          <h2>{{ frame.heading }}</h2>
          <span class="badge">{{ frame.state }}</span>
          <button class="button subtle" :disabled="!selection" @click="replaySelection">Replay from selection</button>
        </div>
        <div class="inspector-tabs" role="tablist">
          <button
            v-for="[value, label] in [
              ['replay', 'Output replay'],
              ['detail', 'Call details & evidence'],
              ['raw', 'Raw records'],
              ['events', 'Event log'],
              ['metrics', 'Metrics'],
            ] as const"
            :key="value"
            role="tab"
            :aria-selected="tab === value"
            @click="inspect(value)"
          >
            {{ label }}
          </button>
        </div>
        <div class="inspector-body" role="tabpanel">
          <template v-if="tab === 'replay'"
            ><p class="output-meta">
              {{
                frame.channel
                  ? `${number(frame.chunks)} recorded output updates through this cursor`
                  : 'Select a model request to replay its output. Open Call details & evidence to inspect a tool or event.'
              }}
            </p>
            <details v-if="frame.reasoning" class="reasoning">
              <summary>Reasoning at cursor</summary>
              <pre>{{ frame.reasoning }}</pre>
            </details>
            <pre v-if="frame.text" class="output-text">{{ frame.text }}</pre>
            <Json v-if="frame.completedOutput !== undefined" :data="frame.completedOutput" />
            <p v-if="frame.channel && !frame.chunks && frame.completedOutput === undefined" class="empty-detail">
              No output has reached this cursor.
            </p></template
          >
          <Evidence
            v-else-if="tab === 'detail' || tab === 'raw'"
            :client="client"
            :frame="frame"
            :clock="summary.clock"
            :raw="tab === 'raw'"
            @select="select"
          />
          <div v-else-if="tab === 'events'" class="airi-event-log">
            <TraceEventLog v-if="log" :log="log" :version="1" :origin="summary.origin" :late="0" @select="select" />
            <p v-else role="status">{{ logLoading ? 'Reading the complete event log…' : 'No event log loaded.' }}</p>
          </div>
          <div v-else>
            <p class="output-meta">Metrics appear when the recording contains samples for its declared metrics.</p>
            <TraceMetrics :state="state" :samples="overview.metricSamples()" :version="version" :locale="locale" />
          </div>
        </div>
      </section>
    </template>
  </main>
  <footer><span>airimayor · AIRI trace-kit / ui-trace</span><span>Local evidence. Recorded facts.</span></footer>
</template>
