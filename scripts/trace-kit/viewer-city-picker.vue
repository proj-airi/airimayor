<script setup lang="ts">
import { computed, nextTick, ref, useId, useTemplateRef } from 'vue'
import { onClickOutside } from '@vueuse/core'
const props = defineProps<{ label: string; items: { key: string; label: string }[] }>()
const value = defineModel<string>({ required: true })
const id = useId()
const open = ref(false),
  search = ref(''),
  page = ref(0),
  active = ref(0),
  root = useTemplateRef<HTMLElement>('root'),
  control = useTemplateRef<HTMLButtonElement>('control'),
  input = useTemplateRef<HTMLInputElement>('input')
onClickOutside(root, () => {
  open.value = false
})
const matches = computed(() => {
  const words = search.value.toLowerCase().split(/\s+/).filter(Boolean)
  return props.items.filter((item) => words.every((word) => `${item.label} ${item.key}`.toLowerCase().includes(word)))
})
async function toggle() {
  open.value = !open.value
  search.value = ''
  page.value = 0
  active.value = 0
  if (open.value) {
    await nextTick()
    input.value?.focus()
  }
}
function choose(key: string) {
  value.value = key
  close()
}
function close() {
  open.value = false
  control.value?.focus({ preventScroll: true })
}
function changePage(next: number) {
  page.value = next
  active.value = next * 30
}
async function move(direction: number) {
  active.value = Math.max(0, Math.min(matches.value.length - 1, active.value + direction))
  page.value = Math.floor(active.value / 30)
  await nextTick()
  root.value?.querySelector(`[data-choice="${active.value}"]`)?.scrollIntoView({ block: 'nearest' })
}
</script>
<template>
  <div ref="root" class="relative min-w-0" @keydown.esc.prevent="close">
    <button
      ref="control"
      role="combobox"
      :aria-label="label"
      :aria-expanded="open"
      :aria-controls="`${id}-list`"
      class="flex w-full min-w-0 items-center gap-2 rounded-md border border-neutral-700 px-3 py-2 text-left text-sm hover:bg-neutral-800"
      @click="toggle"
      @keydown.down.prevent="!open && toggle()"
    >
      <span class="truncate">{{
        items.find((item) => item.key === value)?.label ?? `Choose ${label.toLowerCase()}`
      }}</span
      ><span class="i-mingcute:down-line ml-auto size-3 shrink-0 text-neutral-500" aria-hidden="true" />
    </button>
    <div
      v-if="open"
      class="absolute left-0 top-full z-30 mt-1 w-full min-w-56 rounded-md border border-neutral-700 bg-neutral-900 p-2 shadow-xl"
    >
      <input
        ref="input"
        v-model="search"
        type="search"
        :aria-label="`Search ${label.toLowerCase()}`"
        :aria-controls="`${id}-list`"
        :aria-activedescendant="matches.length ? `${id}-${active}` : undefined"
        :placeholder="`Search ${label.toLowerCase()}…`"
        class="w-full rounded border border-neutral-700 bg-neutral-950 px-2 py-2 text-sm"
        @input="changePage(0)"
        @keydown.down.prevent="move(1)"
        @keydown.up.prevent="move(-1)"
        @keydown.enter.prevent="matches[active] && choose(matches[active].key)"
      />
      <div :id="`${id}-list`" role="listbox" :aria-label="label" class="mt-1 max-h-64 overflow-y-auto">
        <button
          v-for="(item, index) in matches.slice(page * 30, page * 30 + 30)"
          :key="item.key"
          :id="`${id}-${page * 30 + index}`"
          :data-choice="page * 30 + index"
          role="option"
          :aria-selected="item.key === value"
          class="block w-full rounded px-2 py-2 text-left text-sm hover:bg-neutral-800"
          :class="[
            item.key === value ? 'text-pink-300' : 'text-neutral-300',
            page * 30 + index === active ? 'bg-neutral-800' : '',
          ]"
          @click="choose(item.key)"
          @pointermove="active = page * 30 + index"
        >
          {{ item.label }}
        </button>
        <p v-if="!matches.length" class="p-3 text-xs text-neutral-500">No matching {{ label.toLowerCase() }}.</p>
      </div>
      <div
        v-if="matches.length > 30"
        class="mt-1 flex items-center justify-between border-t border-neutral-800 pt-1 text-xs text-neutral-500"
      >
        <button class="viewer-button py-1 text-xs" :disabled="page === 0" @click="changePage(page - 1)">Previous</button
        ><span>{{ page * 30 + 1 }}–{{ Math.min(page * 30 + 30, matches.length) }} / {{ matches.length }}</span
        ><button
          class="viewer-button py-1 text-xs"
          :disabled="page * 30 + 30 >= matches.length"
          @click="changePage(page + 1)"
        >
          Next
        </button>
      </div>
    </div>
  </div>
</template>
