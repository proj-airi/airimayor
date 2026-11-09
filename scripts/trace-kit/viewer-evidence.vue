<script setup lang="ts">
import type { TraceItemRef } from '@proj-airi/trace-kit/views'
import type { ViewerClient } from './viewer-client'
import type { Evidence, Frame } from './viewer-session'
import { createTraceViewReader } from '@proj-airi/trace-kit/views'
import { TraceDetail } from '@proj-airi/ui-trace'
import { VList } from 'virtua/vue'
import { shallowRef, ref, watch } from 'vue'
import Json from './viewer-json.vue'
import RawRecord from './viewer-raw-record.vue'
const props = defineProps<{ client: ViewerClient; frame: Frame; clock: string; raw: boolean }>()
defineEmits<{ select: [selection: TraceItemRef | undefined] }>()
const evidence = shallowRef<Evidence>(),
  view = shallowRef<ReturnType<typeof createTraceViewReader>>(),
  error = ref('')
const expanded = ref(new Set<number>())
const attachments = shallowRef<{ name: string; type: string; url: string }[]>([])
watch(
  () => [props.client, props.frame, props.raw] as const,
  async ([client, frame, raw], _old, onCleanup) => {
    let current = true
    onCleanup(() => {
      current = false
    })
    evidence.value = undefined
    view.value = undefined
    error.value = ''
    expanded.value = new Set()
    try {
      const [records, result] = await Promise.all([
        raw ? Promise.resolve(undefined) : client.remote.snapshot(frame.position),
        client.remote.evidence(frame.selection, frame.position, raw),
      ])
      if (!current) return
      if (records) {
        const reader = createTraceViewReader(props.clock, { seriesRetention: false })
        reader.reader.handleRecords(records)
        view.value = reader
      }
      evidence.value = result
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
function toggle(index: number, event: Event) {
  const next = new Set(expanded.value)
  if ((event.target as HTMLDetailsElement).open) next.add(index)
  else next.delete(index)
  expanded.value = next
}
</script>
<template>
  <p v-if="error" class="error">{{ error }}</p>
  <p v-else-if="!evidence" class="empty-detail" role="status">Reading evidence at the replay cursor…</p>
  <template v-else-if="raw"
    ><p class="raw-count">{{ evidence.rawIndices.length.toLocaleString() }} original records through this cursor.</p>
    <VList :data="evidence.rawIndices" class="raw-list"
      ><template #default="{ item }"><RawRecord :client="client" :index="item" /></template></VList
  ></template>
  <div v-else class="evidence-layout">
    <div v-if="view && frame.item" class="airi-details">
      <TraceDetail
        :state="view.timelineView()"
        :indexes="view.reader.indexes"
        :version="1"
        :selection="frame.item"
        @select="$emit('select', $event)"
      />
    </div>
    <div class="resolved-evidence">
      <h3>Resolved evidence at cursor</h3>
      <p v-if="!evidence.sections.length" class="empty-detail">
        No evidence for this selection has reached the cursor.
      </p>
      <details
        v-for="(section, index) in evidence.sections"
        :key="`${section.title}:${index}`"
        class="evidence-section"
        @toggle="toggle(index, $event)"
      >
        <summary>{{ section.title }}</summary>
        <Json v-if="expanded.has(index)" :data="section.data" />
      </details>
      <div class="attachments">
        <figure v-for="file in attachments" :key="file.url" class="attachment">
          <img v-if="file.type.startsWith('image/')" :src="file.url" :alt="file.name" loading="lazy" /><audio
            v-else-if="file.type.startsWith('audio/')"
            :src="file.url"
            controls
            preload="none"
          /><video v-else-if="file.type.startsWith('video/')" :src="file.url" controls preload="none" />
          <figcaption>
            <a :href="file.url" :download="file.name">{{ file.name }}</a>
          </figcaption>
        </figure>
      </div>
    </div>
  </div>
</template>
