<script setup lang="ts">
import type { TraceItemRef } from '@proj-airi/trace-kit/views'
import type { ViewerClient } from './viewer-client'
import type { Evidence, Frame } from './viewer-session'
import { createTraceViewReader } from '@proj-airi/trace-kit/views'
import { TraceDetail } from '@proj-airi/ui-trace'
import { computed, ref, shallowRef, watch } from 'vue'
import Json from './viewer-json.vue'
import RawRecord from './viewer-raw-record.vue'

const props = defineProps<{ client: ViewerClient; frame: Frame; clock: string; mode: 'detail' | 'evidence' | 'raw' }>()
defineEmits<{ select: [selection: TraceItemRef | undefined] }>()
const evidence = shallowRef<Evidence>(),
  view = shallowRef<ReturnType<typeof createTraceViewReader>>(),
  error = ref(''),
  part = ref(0),
  recordPosition = ref(1)
const attachments = shallowRef<{ name: string; type: string; url: string }[]>([])
const section = computed(() => evidence.value?.sections[part.value])
const attachment = computed(() => attachments.value[part.value - (evidence.value?.sections.length ?? 0)])
const recordIndex = computed(() => evidence.value?.rawIndices[recordPosition.value - 1])

watch(
  () => [props.client, props.frame, props.mode] as const,
  async ([client, frame, mode], _old, onCleanup) => {
    let current = true
    onCleanup(() => {
      current = false
    })
    evidence.value = undefined
    view.value = undefined
    error.value = ''
    part.value = 0
    recordPosition.value = 1
    try {
      if (mode === 'detail') {
        const records = await client.remote.snapshot(frame.position)
        if (!current) return
        const reader = createTraceViewReader(props.clock, { seriesRetention: false })
        reader.reader.handleRecords(records)
        view.value = reader
      } else {
        const result = await client.remote.evidence(frame.selection, frame.position, mode === 'raw')
        if (current) evidence.value = result
      }
    } catch (value) {
      if (current) error.value = String(value)
    }
  },
  { immediate: true },
)
watch(evidence, (value, _old, onCleanup) => {
  const files =
    value?.attachments.map((file) => ({ name: file.name, type: file.type, url: URL.createObjectURL(file.blob) })) ?? []
  attachments.value = files
  onCleanup(() => files.forEach((file) => URL.revokeObjectURL(file.url)))
})
function changeRecord(event: Event) {
  const input = event.target as HTMLInputElement
  if (input.validity.valid) recordPosition.value = input.valueAsNumber
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col overflow-hidden">
    <p v-if="error" class="break-words p-3 text-red-400" role="alert">{{ error }}</p>
    <template v-else-if="mode === 'detail'">
      <TraceDetail
        v-if="view && frame.item"
        class="min-h-0 flex-1 !text-sm [scrollbar-gutter:stable]"
        :state="view.timelineView()"
        :indexes="view.reader.indexes"
        :version="1"
        :selection="frame.item"
        @select="$emit('select', $event)"
      />
      <p v-else class="viewer-empty" role="status">
        {{
          !view ? 'Loading details…' : frame.selection ? 'No call details recorded.' : 'Select a call in the timeline.'
        }}
      </p>
    </template>
    <p v-else-if="!evidence" class="viewer-empty" role="status">Loading evidence…</p>
    <template v-else-if="mode === 'raw'">
      <template v-if="recordIndex !== undefined">
        <div class="flex shrink-0 items-center gap-2 border-b border-neutral-800 px-3 py-2">
          <button
            class="viewer-icon-button"
            :disabled="recordPosition === 1"
            aria-label="Previous record"
            title="Previous record"
            @click="recordPosition--"
          >
            <span class="viewer-icon i-mingcute:left-line" aria-hidden="true" />
          </button>
          <label class="flex items-center gap-2"
            >Record<input
              class="w-20 rounded-md border border-neutral-700 bg-neutral-900 px-2 py-1 tabular-nums"
              type="number"
              min="1"
              :max="evidence.rawIndices.length"
              :value="recordPosition"
              required
              aria-label="Call record number"
              @change="changeRecord"
          /></label>
          <span class="text-neutral-400">/ {{ evidence.rawIndices.length.toLocaleString() }}</span>
          <button
            class="viewer-icon-button"
            :disabled="recordPosition === evidence.rawIndices.length"
            aria-label="Next record"
            title="Next record"
            @click="recordPosition++"
          >
            <span class="viewer-icon i-mingcute:right-line" aria-hidden="true" />
          </button>
        </div>
        <RawRecord :key="recordIndex" :client="client" :index="recordIndex" class="min-h-0 flex-1" />
      </template>
      <p v-else class="viewer-empty">No records at this point.</p>
    </template>
    <template v-else>
      <template v-if="evidence.sections.length || attachments.length">
        <div class="shrink-0 border-b border-neutral-800 p-3">
          <select
            v-model="part"
            class="w-full rounded-md border border-neutral-700 bg-neutral-900 px-2 py-1"
            aria-label="Evidence section"
          >
            <option v-for="(item, index) in evidence.sections" :key="index" :value="index">{{ item.title }}</option>
            <option v-for="(file, index) in attachments" :key="file.url" :value="evidence.sections.length + index">
              {{ file.type }} · {{ file.name.slice(0, 8) }}
            </option>
          </select>
        </div>
        <Json v-if="section" :key="part" :data="section.data" class="min-h-0 flex-1" />
        <div v-else-if="attachment" class="min-h-0 flex-1 overflow-y-auto p-3">
          <figure>
            <img
              v-if="attachment.type.startsWith('image/')"
              class="max-w-full"
              :src="attachment.url"
              :alt="attachment.name"
              loading="lazy"
            />
            <audio
              v-else-if="attachment.type.startsWith('audio/')"
              class="max-w-full"
              :src="attachment.url"
              controls
              preload="none"
            />
            <video
              v-else-if="attachment.type.startsWith('video/')"
              class="max-w-full"
              :src="attachment.url"
              controls
              preload="none"
            />
            <figcaption class="mt-3 break-words">
              <a
                class="text-pink-300 hover:underline"
                :href="attachment.url"
                :download="attachment.name"
                :title="attachment.name"
                >Download {{ attachment.type }}</a
              >
            </figcaption>
          </figure>
        </div>
      </template>
      <p v-else class="viewer-empty">
        {{ frame.selection ? 'No evidence at this point.' : 'Select a call in the timeline.' }}
      </p>
    </template>
  </div>
</template>
