<script setup lang="ts">
import { ScrollableArea } from '@proj-airi/ui'
import { useEventListener, useResizeObserver } from '@vueuse/core'
import { computed, ref, useTemplateRef } from 'vue'

const props = defineProps<{
  label: string
  sections: { id: string; label: string; title?: string }[]
}>()
const area = useTemplateRef<InstanceType<typeof ScrollableArea>>('area')
const content = useTemplateRef<HTMLElement>('content')
const viewport = computed(() => area.value?.viewport)
const active = ref(props.sections[0]?.id)
const height = ref(0)

function sectionElements() {
  return Array.from(content.value?.querySelectorAll<HTMLElement>(':scope > section') ?? [])
}

function updateActive() {
  const element = viewport.value
  if (!element?.clientHeight) return
  height.value = element.clientHeight
  const top = element.getBoundingClientRect().top + 24
  active.value =
    sectionElements().findLast((section) => section.getBoundingClientRect().top <= top)?.id ??
    props.sections[0]?.id
}

function goTo(id: string) {
  const element = viewport.value
  const section = sectionElements().find((section) => section.id === id)
  if (!element || !section) return
  element.scrollTo({
    top: element.scrollTop + section.getBoundingClientRect().top - element.getBoundingClientRect().top - 12,
  })
  section.querySelector('h2')?.focus({ preventScroll: true })
  updateActive()
}

useEventListener(viewport, 'scroll', updateActive, { passive: true })
useResizeObserver(viewport, updateActive)
useResizeObserver(content, updateActive)
</script>

<template>
  <div class="report h-full min-h-0 min-w-0">
    <div class="report-layout">
      <nav class="report-nav" :aria-label="`${label} sections`">
        <a
          v-for="section in props.sections"
          :key="section.id"
          :href="`#${section.id}`"
          class="report-link viewer-button justify-start px-3 py-2 text-xs"
          :aria-current="active === section.id ? 'location' : undefined"
          @click.prevent="goTo(section.id)"
        >
          {{ section.label }}
        </a>
      </nav>
      <ScrollableArea
        ref="area"
        class="min-h-0 min-w-0 flex-1"
        viewport-class="overscroll-contain"
        role="region"
        :aria-label="`${label} report`"
      >
        <div ref="content" class="space-y-8 p-3 text-sm">
          <section
            v-for="(section, index) in props.sections"
            :id="section.id"
            :key="section.id"
            :aria-labelledby="`${section.id}-heading`"
            class="space-y-3"
            :style="
              index === props.sections.length - 1 ? { minHeight: `${Math.max(0, height - 24)}px` } : undefined
            "
          >
            <h2 :id="`${section.id}-heading`" tabindex="-1" class="font-medium outline-none">
              {{ section.title ?? section.label }}
            </h2>
            <slot :name="section.id" />
          </section>
        </div>
      </ScrollableArea>
    </div>
  </div>
</template>

<style scoped>
.report {
  container-type: inline-size;
}
.report-layout {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}
.report-nav {
  display: flex;
  flex-shrink: 0;
  flex-wrap: wrap;
  gap: 0.25rem;
  padding: 0.5rem;
  border-bottom: 1px solid #262626;
}
.report-link[aria-current='location'] {
  color: #f9a8d4;
  background: rgb(249 168 212 / 10%);
}
@container (min-width: 640px) {
  .report-layout {
    flex-direction: row;
  }
  .report-nav {
    flex-direction: column;
    flex-wrap: nowrap;
    width: 9rem;
    overflow-y: auto;
    border-right: 1px solid #262626;
    border-bottom: 0;
  }
}
</style>
