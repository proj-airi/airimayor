<script setup lang="ts">
import type { WorkbenchApi } from '@proj-airi/ui-panes'
import { Workbench } from '@proj-airi/ui-panes'
import { useMediaQuery } from '@vueuse/core'
import { onBeforeUnmount, watch } from 'vue'

export type ViewerPanel = 'timeline' | 'events' | 'metrics' | 'search' | 'city'
const props = defineProps<{ inspecting: boolean }>()
const active = defineModel<ViewerPanel>({ required: true })
const wide = useMediaQuery('(min-width: 900px)')
const panels = ['timeline', 'events', 'metrics', 'search', 'city', 'inspector']
const titles = {
  timeline: 'Timeline',
  events: 'Event log',
  metrics: 'Metrics',
  search: 'Search',
  city: 'City',
}
let api: WorkbenchApi | undefined
let stopActive: (() => void) | undefined

function placeInspector() {
  if (!api) return
  const existing = api.getPanel('inspector')
  if (!props.inspecting) {
    existing?.api.close()
    api.getPanel(active.value)?.api.setActive()
    return
  }
  const anchor = api.getPanel(active.value)!
  const panel =
    existing ??
    api.addPanel({
      id: 'inspector',
      component: 'inspector',
      title: 'Selected call',
      minimumWidth: 260,
      position: { referencePanel: anchor, direction: wide.value ? 'right' : 'within' },
    })
  if (existing) panel.api.moveTo({ group: anchor.group, position: wide.value ? 'right' : 'center' })
  if (wide.value) panel.group.api.setSize({ width: Math.min(420, Math.round(api.width * 0.3)) })
  panel.api.setActive()
}

function ready(value: WorkbenchApi) {
  api = value
  for (const id of Object.keys(titles) as ViewerPanel[]) {
    api.addPanel({
      id,
      component: id,
      title: titles[id],
      minimumWidth: 260,
      renderer: id === 'metrics' ? 'always' : undefined,
      ...(id === 'timeline'
        ? {}
        : { position: { referencePanel: 'timeline', direction: 'within' as const }, inactive: true }),
    })
  }
  api.getPanel(active.value)!.api.setActive()
  const subscription = api.onDidActivePanelChange(({ panel }) => {
    if (panel && panel.id !== 'inspector') active.value = panel.id as ViewerPanel
  })
  stopActive = () => subscription.dispose()
  placeInspector()
}
watch(() => props.inspecting, placeInspector)
watch(wide, () => {
  if (!api) return
  if (!wide.value) {
    const group = api.getPanel(active.value)!.group
    for (const id of Object.keys(titles))
      api.getPanel(id)!.api.moveTo({ group, position: 'center', skipSetActive: true })
  }
  placeInspector()
})
watch(active, (id) => api?.getPanel(id)?.api.setActive())
onBeforeUnmount(() => stopActive?.())
</script>

<template>
  <Workbench :panels="panels" :padded="false" @ready="ready">
    <template v-for="panel in panels" :key="panel" #[panel]><slot :name="panel" /></template>
  </Workbench>
</template>
