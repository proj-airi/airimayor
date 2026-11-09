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
const tab = ref<'replay' | 'detail' | 'raw'>('replay')
const view = ref<'timeline' | 'events' | 'metrics'>('timeline')
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
  <header class="app-header">
    <a class="brand" href="https://github.com/proj-airi/airimayor" target="_blank" rel="noreferrer">AIRI Mayor</a>
    <span class="header-divider" aria-hidden="true">/</span>
    <h1>Trace</h1>
    <span v-if="summary" class="session-name" :title="summary.name">{{ summary.name }}</span>
    <span v-else class="offline-label">Offline viewer</span>
    <div v-if="summary || loading || error" class="import-actions">
      <button
        class="button subtle"
        title="Open records.jsonl without attachments"
        :disabled="loading"
        @click="recordInput?.click()"
      >
        <span class="i-mingcute:file-import-line" aria-hidden="true" />Open file
      </button>
      <button class="button primary" :disabled="loading" @click="chooseFolder">
        <span class="i-mingcute:folder-open-line" aria-hidden="true" />Open session
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
  <main :class="{ 'has-session': summary }">
    <section v-if="!summary && !loading && !error" class="empty-state">
      <div class="trace-motif" aria-hidden="true">
        <div class="motif-axis"><span>0</span><span>5</span><span>10</span><span>15s</span></div>
        <div class="motif-grid">
          <span class="motif-bar motif-root" /><span class="motif-link" /> <span class="motif-bar motif-model" /><span
            class="motif-bar motif-tool"
          />
          <span class="motif-bar motif-reply" /><span class="motif-cursor" />
        </div>
      </div>
      <h2>Trace</h2>
      <p>Open a session record.</p>
      <div class="empty-actions">
        <button class="button primary" @click="chooseFolder">
          <span class="i-mingcute:folder-open-line" aria-hidden="true" />Open session folder
        </button>
        <button class="button subtle" title="Open records.jsonl without attachments" @click="recordInput?.click()">
          <span class="i-mingcute:file-import-line" aria-hidden="true" />Open file
        </button>
      </div>
      <span class="file-hint"><code>records.jsonl</code><span aria-hidden="true"> + </span><code>assets/</code></span>
    </section>
    <div v-if="error" id="import-status" class="error-message" role="alert">
      <span class="i-mingcute:alert-line" aria-hidden="true" />
      <p>{{ error }}</p>
    </div>
    <section v-if="loading" class="import-loading" role="status">
      <span class="i-mingcute:loading-line loading-icon" aria-hidden="true" />
      <h2>{{ progress ? phases[progress.phase] : 'Opening session…' }}</h2>
      <progress id="import-progress" :max="progress?.total || 1" :value="progress?.completed ?? 0" />
      <span v-if="progress" class="mono">{{ number(progress.completed) }} / {{ number(progress.total) }}</span>
      <button class="button subtle" @click="cancelImport">Cancel</button>
    </section>
    <template v-if="summary && frame && client">
      <div class="session-summary">
        <div class="stats">
          <span
            ><span class="i-mingcute:time-line" aria-hidden="true" /><b>{{ elapsed(summary.duration) }}</b></span
          >
          <span
            ><b>{{ number(summary.models) }}</b> model calls</span
          >
          <span
            ><b>{{ number(summary.tools) }}</b> tool calls</span
          >
          <span
            ><b>{{ number(summary.records) }}</b> records</span
          >
        </div>
        <span
          v-if="!summary.issues.length"
          class="validation-status"
          title="Records and referenced files passed validation."
          ><span class="i-mingcute:check-circle-line" aria-hidden="true" />Files checked</span
        >
        <details v-else class="issues">
          <summary>{{ number(summary.issues.length) }} validation issues</summary>
          <div class="issue-list">
            <VList :data="summary.issues" class="issue-items"
              ><template #default="{ item }"
                ><p>{{ item }}</p></template
              ></VList
            >
          </div>
        </details>
      </div>
      <div class="trace-workbench">
        <section class="explorer panel" aria-label="Session trace">
          <div class="panel-toolbar">
            <div class="panel-tabs" role="tablist" aria-label="Trace views">
              <button
                v-for="[value, label, icon] in [
                  ['timeline', 'Timeline', 'i-mingcute:timeline-line'],
                  ['events', 'Event log', 'i-mingcute:list-ordered-line'],
                  ['metrics', 'Metrics', 'i-mingcute:chart-bar-line'],
                ] as const"
                :id="`view-tab-${value}`"
                :key="value"
                role="tab"
                :aria-selected="view === value"
                aria-controls="trace-view"
                @click="view = value"
              >
                <span :class="icon" aria-hidden="true" />{{ label }}
              </button>
            </div>
            <div v-if="view === 'timeline'" class="timeline-actions">
              <button
                class="icon-button"
                :class="{ active: follow }"
                :aria-pressed="follow"
                aria-label="Follow playback"
                title="Follow playback"
                @click="follow = !follow"
              >
                <span class="i-mingcute:aiming-2-line" aria-hidden="true" />
              </button>
              <button class="icon-button" aria-label="Fit timeline" title="Fit timeline" @click="fitTimeline">
                <span class="i-mingcute:fullscreen-2-line" aria-hidden="true" />
              </button>
            </div>
          </div>
          <div id="trace-view" class="explorer-body" role="tabpanel" :aria-labelledby="`view-tab-${view}`">
            <div v-show="view === 'timeline'" class="airi-timeline">
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
                <template #status
                  ><span class="timeline-status"
                    ><span class="status-dot" :class="{ playing }" />{{ playing ? 'Playing' : 'Paused' }}</span
                  ></template
                >
              </TraceTimeline>
            </div>
            <div v-if="view === 'events'" class="airi-event-log">
              <TraceEventLog v-if="log" :log="log" :version="1" :origin="summary.origin" :late="0" @select="select" />
              <p v-else class="empty-detail" role="status">
                {{ logLoading ? 'Loading events…' : 'No events.' }}
              </p>
            </div>
            <div v-if="view === 'metrics'" class="metrics-view">
              <TraceMetrics :state="state" :samples="overview.metricSamples()" :version="version" :locale="locale" />
              <p v-if="!Object.values(overview.metricSamples()).some((samples) => samples.length)" class="empty-detail">
                No metric samples recorded.
              </p>
            </div>
          </div>
        </section>
        <section class="inspector panel" aria-label="Selected call">
          <div class="panel-toolbar">
            <div class="panel-tabs" role="tablist" aria-label="Call views">
              <button
                v-for="[value, label] in [
                  ['replay', 'Output'],
                  ['detail', 'Details'],
                  ['raw', 'Records'],
                ] as const"
                :id="`inspector-tab-${value}`"
                :key="value"
                role="tab"
                :aria-selected="tab === value"
                aria-controls="call-view"
                @click="inspect(value)"
              >
                {{ label }}
              </button>
            </div>
            <button
              class="icon-button"
              :disabled="!selection"
              aria-label="Play from selection"
              title="Play from selection"
              @click="replaySelection"
            >
              <span class="i-mingcute:back-2-line" aria-hidden="true" />
            </button>
          </div>
          <div v-if="tab !== 'detail' || !frame.item" class="inspector-header">
            <h2>{{ frame.selection ? frame.heading : 'No selection' }}</h2>
            <span
              v-if="frame.selection"
              class="call-status"
              :class="{
                completed: frame.state === 'ok' || frame.state === 'Completed',
                failed: frame.state === 'error',
                pending: ['Not reached', 'Incomplete', 'In progress'].includes(frame.state),
              }"
              ><span class="status-dot" />{{ statusLabels[frame.state] ?? frame.state }}</span
            >
          </div>
          <div
            id="call-view"
            class="inspector-body"
            :class="{ 'show-details': tab === 'detail' }"
            role="tabpanel"
            :aria-labelledby="`inspector-tab-${tab}`"
          >
            <template v-if="tab === 'replay'">
              <details v-if="frame.reasoning" class="reasoning">
                <summary>Reasoning</summary>
                <pre>{{ frame.reasoning }}</pre>
              </details>
              <pre v-if="frame.text" class="output-text">{{ frame.text }}</pre>
              <Json v-if="frame.completedOutput !== undefined" :data="frame.completedOutput" />
              <div v-if="!frame.text && !frame.reasoning && frame.completedOutput === undefined" class="empty-detail">
                <span class="i-mingcute:chat-3-line empty-icon" aria-hidden="true" />
                <p>
                  {{
                    !frame.selection
                      ? 'Select a call in the timeline.'
                      : frame.channel
                        ? 'No output at this point.'
                        : 'No text output for this call.'
                  }}
                </p>
                <button v-if="frame.selection && !frame.channel" class="button subtle" @click="inspect('detail')">
                  View details<span class="i-mingcute:arrow-right-line" aria-hidden="true" />
                </button>
              </div>
            </template>
            <Evidence
              v-else
              :client="client"
              :frame="frame"
              :clock="summary.clock"
              :raw="tab === 'raw'"
              @select="select"
            />
          </div>
        </section>
      </div>
      <section class="playback" aria-label="Replay controls">
        <div class="transport-buttons">
          <button
            class="icon-button"
            aria-label="First event"
            title="First event"
            :disabled="frame.position === 0"
            @click="seek(0)"
          >
            <span class="i-mingcute:skip-previous-line" aria-hidden="true" />
          </button>
          <button
            class="icon-button"
            aria-label="Previous event"
            title="Previous event"
            :disabled="frame.position === 0"
            @click="seek(frame.position - 1)"
          >
            <span class="i-mingcute:left-line" aria-hidden="true" />
          </button>
          <button
            class="icon-button play-button"
            :aria-label="playing ? 'Pause' : 'Play'"
            :title="playing ? 'Pause' : 'Play'"
            @click="togglePlay"
          >
            <span :class="playing ? 'i-mingcute:pause-fill' : 'i-mingcute:play-fill'" aria-hidden="true" />
          </button>
          <button
            class="icon-button"
            aria-label="Next event"
            title="Next event"
            :disabled="frame.position === summary.events.length - 1"
            @click="seek(frame.position + 1)"
          >
            <span class="i-mingcute:right-line" aria-hidden="true" />
          </button>
          <button
            class="icon-button"
            aria-label="Last event"
            title="Last event"
            :disabled="frame.position === summary.events.length - 1"
            @click="seek(summary.events.length - 1)"
          >
            <span class="i-mingcute:skip-forward-line" aria-hidden="true" />
          </button>
        </div>
        <span class="cursor-time mono">{{ elapsed(frame.time) }}</span>
        <div class="seek-control">
          <input
            type="range"
            min="0"
            :max="summary.events.length - 1"
            :value="frame.position"
            aria-label="Replay event position"
            title="Replay in recorded order; gaps longer than 1.5 seconds are shortened."
            @input="seek(Number(($event.target as HTMLInputElement).value))"
          />
        </div>
        <span class="duration mono">{{ elapsed(summary.duration) }}</span>
        <select v-model="speed" aria-label="Playback speed" @change="changeSpeed">
          <option v-for="value in [0.5, 1, 2, 4, 8]" :key="value" :value="value">{{ value }}×</option>
        </select>
        <span
          class="event-position mono"
          :title="`Event ${number(frame.position + 1)} of ${number(summary.events.length)}`"
          >{{ number(frame.position + 1) }} / {{ number(summary.events.length) }}</span
        >
      </section>
    </template>
  </main>
</template>
