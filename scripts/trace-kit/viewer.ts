import type { TraceAssetRef, TraceRecord } from '@proj-airi/trace-kit'
import type { LocalFile, OutputChannel, Recording, Span } from './viewer-model'
import { spanKeyOf } from '@proj-airi/trace-kit'
import { spansOfLink } from '@proj-airi/trace-kit/views'
import { isAsset, isObject, loadRecording, payload, Replay } from './viewer-model'

const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T
const escape = (value: unknown) => String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]!)
const pretty = (value: unknown) => JSON.stringify(value, null, 2) ?? String(value)
const seconds = (value: number) => `${(Math.max(0, value) / 1000).toFixed(3)}s`
type Selection = { kind: 'span'; key: string } | { kind: 'record'; index: number } | { kind: 'acp'; agentSession: string }
type Tab = 'replay' | 'evidence' | 'raw'
let recording: Recording | undefined
let replay: Replay | undefined
let selection: Selection | undefined
let tab: Tab = 'replay'
let eventPosition = 0
let playing = false
let timer: ReturnType<typeof setTimeout> | undefined
let urls: string[] = []
let detailGeneration = 0
const eventPageSize = 200
let eventMatches: number[] = []
let eventPage = 0

function originTime() { return recording ? recording.full.reader.timeOf(recording.records[0]).time : 0 }
function timeOf(index: number) { return recording!.full.reader.timeOf(recording!.records[index]).time - originTime() }
function agentOf(record: TraceRecord) {
  return String(record.ids?.agent ?? ('track' in record && record.track.startsWith('agents/') ? record.track.split('/')[1] : 'City'))
}

function recordTitle(record: TraceRecord) {
  const names: Record<string, string> = {
    'source.started': 'Recording started', 'source.stopped': 'Recording closed',
    'input.received': 'Player input received', 'input.queued': 'Player input queued', 'input.applied': 'Input sent to model',
    'stop.requested': 'Stop requested', 'stop.applied': 'Stop applied', 'plan.changed': 'Plan updated',
    'compaction.started': 'Compaction started', 'compaction.finished': 'Compaction finished', 'history.repaired': 'History repaired',
    'city.tool.result': 'City tool result', 'recording.gap': 'Known recording gap', 'session.fact': 'Session facts',
    'model.output.delta': 'Model output update', 'acp.output.delta': 'External output update',
  }
  if (record.type === 'span.begin' || record.type === 'span.complete') return record.name
  if (record.type === 'span.end') return `Finished · ${record.status}`
  if (record.type === 'span.update') return 'Input / attributes captured'
  if (record.type === 'log') return `${record.level} · ${record.message}`
  if (record.type === 'instant') {
    const data = recording ? payload(recording, record) : {}
    const suffix = typeof data.index === 'number' ? ` #${data.index + 1}` : ''
    return `${names[record.name] ?? record.name}${suffix}`
  }
  return names[record.type] ?? record.type
}

function classOf(track = '') { return track.endsWith('/llm') ? 'model' : track.endsWith('/tools') ? 'tool' : 'turn' }
function spanState(span: Span | undefined) {
  if (!span) return { text: 'Not reached', css: 'active' }
  if (span.abandoned || (span.end === undefined && replay!.cursor === recording!.records.length - 1))
    return { text: 'Incomplete', css: 'cancelled' }
  if (span.end === undefined) return { text: 'In progress', css: 'active' }
  return { text: span.status === 'error' ? 'Error' : span.status === 'cancelled' ? 'Cancelled' : 'Completed', css: span.status ?? '' }
}

function stop() {
  playing = false
  clearTimeout(timer)
  $('play').textContent = '▶ Play'
  $('play').setAttribute('aria-label', 'Play recording')
}

function selectionAt(index: number): Selection | undefined {
  if (!recording) return
  const record = recording.records[index]
  if (record.type === 'instant' && record.name === 'acp.output.delta') {
    const data = payload(recording, record)
    return { kind: 'acp', agentSession: String(data.agentSession ?? record.ids?.agentSession ?? '') }
  }
  if ('spanId' in record && record.spanId && recording.full.timelineView().spans.has(spanKeyOf(record.source, record.epoch, record.spanId)))
    return { kind: 'span', key: spanKeyOf(record.source, record.epoch, record.spanId) }
  if (record.type === 'instant' || record.type === 'log') return { kind: 'record', index }
}

function seek(position: number, follow = true) {
  if (!recording || !replay) return
  eventPosition = Math.max(0, Math.min(recording.events.length - 1, position))
  replay.seek(recording.events[eventPosition])
  if (follow && $<HTMLInputElement>('follow').checked) {
    const candidate = selectionAt(replay.cursor)
    const record = recording.records[replay.cursor]
    if (candidate && !(candidate.kind === 'span' && 'track' in record && record.track.endsWith('/turn')))
      selection = candidate
  }
  $<HTMLInputElement>('seek').value = String(eventPosition)
  $('cursor-label').textContent = `${seconds(timeOf(replay.cursor))}  /  event ${eventPosition + 1} of ${recording.events.length}`
  if (follow && $<HTMLInputElement>('follow').checked) {
    const match = eventMatches.indexOf(eventPosition)
    if (match >= 0 && Math.floor(match / eventPageSize) !== eventPage) {
      eventPage = Math.floor(match / eventPageSize)
      renderEvents()
    }
  }
  for (const row of $('event-list').querySelectorAll<HTMLElement>('[data-event]')) {
    const index = Number(row.dataset.event)
    row.classList.toggle('current', index === eventPosition)
    row.classList.toggle('future', index > eventPosition)
    row.setAttribute('aria-current', index === eventPosition ? 'step' : 'false')
    if (index === eventPosition && follow && $<HTMLInputElement>('follow').checked)
      row.scrollIntoView({ block: 'nearest', behavior: 'instant' })
  }
  $('first').toggleAttribute('disabled', eventPosition === 0)
  $('previous').toggleAttribute('disabled', eventPosition === 0)
  $('last').toggleAttribute('disabled', eventPosition === recording.events.length - 1)
  $('next').toggleAttribute('disabled', eventPosition === recording.events.length - 1)
  renderTimeline()
  renderInspector()
}

function tick() {
  if (!playing || !recording) return
  if (eventPosition === recording.events.length - 1) { stop(); return }
  const delay = Math.min(1500, Math.max(35,
    (timeOf(recording.events[eventPosition + 1]) - timeOf(recording.events[eventPosition])) / Number($<HTMLSelectElement>('speed').value)))
  timer = setTimeout(() => { seek(eventPosition + 1); tick() }, delay)
}

function play() {
  if (!recording) return
  if (playing) { stop(); return }
  if (eventPosition === recording.events.length - 1) seek(0)
  playing = true
  $('play').textContent = 'Ⅱ Pause'
  $('play').setAttribute('aria-label', 'Pause recording')
  tick()
}

function selectEvent(position: number) {
  if (!recording) return
  stop()
  const index = recording.events[position]
  selection = selectionAt(index) ?? { kind: 'record', index }
  seek(position)
}

function renderEvents() {
  if (!recording) return
  const start = eventPage * eventPageSize
  const rows = eventMatches.slice(start, start + eventPageSize).map(position => {
    const index = recording!.events[position]
    const record = recording!.records[index]
    const name = recordTitle(record)
    const agent = agentOf(record)
    return `<button class="event-row${position === eventPosition ? ' current' : ''}${position > eventPosition ? ' future' : ''}" role="listitem" data-event="${position}" aria-label="Event ${position + 1}: ${escape(name)}"><span class="event-time">${seconds(timeOf(index))}</span><span><span class="event-name"><i class="event-kind ${classOf('track' in record ? record.track : '')}" aria-hidden="true"></i>${escape(name)}</span><span class="event-agent">${escape(agent)} · ${escape(record.type)} · #${record.seq}</span></span></button>`
  })
  $('event-list').innerHTML = rows.join('') || '<p class="event-empty">No events match this filter.</p>'
  $('event-count').textContent = `(${eventMatches.length})`
  $('event-page').textContent = eventMatches.length ? `${start + 1}–${start + rows.length} of ${eventMatches.length}` : '0 events'
  $('events-earlier').toggleAttribute('disabled', eventPage === 0)
  $('events-later').toggleAttribute('disabled', start + eventPageSize >= eventMatches.length)
}

function filterEvents() {
  if (!recording) return
  const query = $<HTMLInputElement>('event-search').value.toLowerCase()
  eventMatches = recording.events.flatMap((index, position) => {
    const record = recording!.records[index]
    return !query || `${recordTitle(record)} ${agentOf(record)} ${record.type} ${record.ids?.agentSession ?? ''}`.toLowerCase().includes(query) ? [position] : []
  })
  eventPage = Math.floor(Math.max(0, eventMatches.indexOf(eventPosition)) / eventPageSize)
  renderEvents()
}

function renderTimeline() {
  if (!recording || !replay) return
  const full = recording.full.timelineView()
  const current = replay.view.timelineView()
  const spans = [...full.spans.values()]
  type Lane = { key: string; agent: string; spans: Span[]; events: number[] }
  const lanes = new Map<string, Lane>()
  for (const span of spans) {
    const agent = String(span.ids?.agent ?? span.track.split('/')[1] ?? 'City')
    const key = String(span.ids?.agentSession ?? agent)
    if (!lanes.has(key)) lanes.set(key, { key, agent, spans: [], events: [] })
    lanes.get(key)!.spans.push(span)
  }
  recording.records.forEach((record, index) => {
    if (record.type !== 'instant' || record.name !== 'acp.output.delta') return
    const data = payload(recording!, record)
    const key = `acp:${data.agentSession}`
    if (!lanes.has(key)) lanes.set(key, { key, agent: 'External mayor', spans: [], events: [] })
    lanes.get(key)!.events.push(index)
  })
  if (!lanes.size) lanes.set('city', { key: 'city', agent: 'City events', spans: [], events: [] })
  const ordered = [...lanes.values()].sort((a, b) => Number(b.agent === 'mayor') - Number(a.agent === 'mayor'))
  const width = 1100, left = 188, right = 18, top = 32, laneHeight = 82
  const height = ordered.length * laneHeight + top + 13
  const duration = recording.records.reduce((longest, _, index) => Math.max(longest, timeOf(index)), 1)
  const x = (time: number) => left + Math.max(0, time) / duration * (width - left - right)
  const coordinates = new Map<string, { x: number; y: number; end: number; lane: string }>()
  let content = `<defs><marker id="arrow" markerWidth="7" markerHeight="7" refX="6" refY="3" orient="auto"><path d="M0 0 L6 3 L0 6" fill="none" stroke="#d7ad6a"/></marker></defs>`
  for (let i = 0; i <= 5; i++) {
    const at = i / 5 * duration
    content += `<line class="grid-line" x1="${x(at)}" x2="${x(at)}" y1="25" y2="${height - 10}"/><text class="axis-label" x="${x(at)}" y="14" text-anchor="${i === 5 ? 'end' : 'start'}">${seconds(at)}</text>`
  }
  ordered.forEach((lane, index) => {
    const y = top + index * laneHeight
    const label = lane.agent === 'mayor' ? 'Mayor' : lane.agent === 'inspector' ? 'Background inspector' : lane.agent
    const sub = lane.key === lane.agent ? 'Root conversation' : lane.key.replace('acp:', '').slice(0, 16)
    content += `<rect class="lane-bg" x="0" y="${y - 7}" width="${width}" height="${laneHeight - 5}" rx="5"/><text class="lane-label" x="13" y="${y + 17}">${escape(label)}</text><text class="lane-sub" x="13" y="${y + 35}">${escape(sub)}</text>`
    for (const span of lane.spans) {
      const kind = classOf(span.track)
      const sy = y + (kind === 'model' ? 23 : kind === 'tool' ? 43 : 3)
      const start = x(span.start - originTime())
      const end = x((span.end ?? (duration + originTime())) - originTime())
      const barWidth = Math.max(3, end - start)
      coordinates.set(span.key, { x: start, y: sy + 7, end, lane: lane.key })
      const live = current.spans.get(span.key)
      const state = spanState(live)
      const selected = selection?.kind === 'span' && selection.key === span.key
      const status = state.css === 'error' ? ' error' : state.text === 'Incomplete' ? ' incomplete' : ''
      content += `<g class="span-item${selected ? ' selected' : ''}${!live ? ' future' : ''}" data-span="${escape(span.key)}" tabindex="0" role="button" aria-label="${escape(`${label}: ${span.name}, ${state.text}`)}"><title>${escape(`${span.name} · ${state.text}`)}</title><rect class="span-bar ${kind}${status}" x="${start}" y="${sy}" width="${barWidth}" height="14" rx="3"/>${barWidth > 88 ? `<text class="bar-label" x="${start + 5}" y="${sy + 10}">${escape(span.name.slice(0, Math.floor(barWidth / 5.4)))}</text>` : ''}</g>`
    }
    for (const event of lane.events) {
      const position = recording!.events.indexOf(event)
      content += `<circle class="acp-dot" cx="${x(timeOf(event))}" cy="${y + 27}" r="4" data-event="${position}" tabindex="0" role="button" aria-label="External output update at ${seconds(timeOf(event))}"/>`
    }
  })
  let links = ''
  for (const span of spans) {
    const source = coordinates.get(span.key)
    if (!source) continue
    const targets = span.links.flatMap(link => spansOfLink(link, full, recording!.full.reader.indexes).map(target => ({ target, rel: link.rel })))
    if (span.parentKey && full.spans.has(span.parentKey)) targets.push({ target: full.spans.get(span.parentKey)!, rel: 'parent' })
    for (const { target, rel } of targets) {
      const cause = coordinates.get(target.key)
      if (!cause || source.lane === cause.lane) continue
      const bend = Math.max(cause.x + 10, source.x - 17)
      links += `<path class="causal-link" marker-end="url(#arrow)" d="M${cause.x},${cause.y} C${bend},${cause.y} ${bend},${source.y} ${source.x},${source.y}"><title>${escape(`${rel}: ${span.name} → ${target.name}`)}</title></path>`
    }
  }
  const cursor = x(timeOf(replay.cursor))
  content += links + `<line class="cursor-line" x1="${cursor}" x2="${cursor}" y1="23" y2="${height - 6}"/><path class="cursor-head" d="M${cursor - 4} 18 L${cursor + 4} 18 L${cursor} 24 Z"/>`
  $('timeline').innerHTML = `<svg viewBox="0 0 ${width} ${height}" role="group" aria-label="${ordered.length} execution lanes">${content}</svg>`
}

function section(title: string, value: unknown, open = false) {
  if (value === undefined) return ''
  const resolved = recording!.assets.resolve(value)
  return `<details class="evidence-section"${open ? ' open' : ''}><summary>${escape(title)}</summary><div><pre>${escape(typeof resolved === 'string' ? resolved : pretty(resolved))}</pre><div class="attachments" data-attachments></div></div></details>`
}

function refsOf(value: unknown, refs = new Map<string, TraceAssetRef>()) {
  if (isAsset(value)) {
    const key = `${value.$asset}\0${value.mediaType}`
    if (refs.has(key)) return refs
    refs.set(key, value)
    refsOf(recording!.assets.data(value), refs)
  }
  else if (Array.isArray(value)) for (const item of value) refsOf(item, refs)
  else if (isObject(value)) for (const item of Object.values(value)) refsOf(item, refs)
  return refs
}

async function renderAttachments(value: unknown, generation: number) {
  const refs = [...refsOf(value).values()].filter(ref => ref.mediaType !== 'application/json')
  if (!refs.length) return
  const target = document.createElement('div')
  target.className = 'attachments'
  for (const ref of refs) {
    const file = recording!.assets.file(ref)
    const card = document.createElement('div')
    card.className = 'attachment'
    if (!file) { card.textContent = `Missing ${ref.mediaType} attachment`; target.append(card); continue }
    const bytes = await file.read()
    if (generation !== detailGeneration) return
    const url = URL.createObjectURL(new Blob([bytes as Uint8Array<ArrayBuffer>], { type: ref.mediaType }))
    urls.push(url)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = ref.$asset.split('/').at(-1)!
    anchor.textContent = `${ref.mediaType} · ${ref.bytes.toLocaleString()} bytes ↗`
    card.append(anchor)
    if (ref.mediaType.startsWith('image/')) {
      const image = document.createElement('img')
      image.src = url
      image.alt = `Recorded ${ref.mediaType} attachment`
      card.prepend(image)
    }
    else if (ref.mediaType.startsWith('text/')) {
      const pre = document.createElement('pre')
      pre.textContent = new TextDecoder().decode(bytes)
      card.append(pre)
    }
    target.append(card)
  }
  if (generation === detailGeneration) $('inspector-body').append(target)
}

function recordsForSelection(): { span?: Span; records: { record: TraceRecord; index: number }[]; channel?: OutputChannel } {
  if (!recording || !selection || !replay) return { records: [] }
  if (selection.kind === 'record') return { records: selection.index <= replay.cursor ? [{ record: recording.records[selection.index], index: selection.index }] : [] }
  if (selection.kind === 'acp') {
    const agentSession = selection.agentSession
    return { channel: { kind: 'acp', agentSession }, records: recording.records.flatMap((record, index) => index <= replay!.cursor
      && (record.ids?.agentSession === agentSession || payload(recording!, record).agentSession === agentSession) ? [{ record, index }] : []) }
  }
  const full = recording.full.timelineView().spans.get(selection.key)!
  const span = replay.view.timelineView().spans.get(selection.key)
  const records = recording.records.flatMap((record, index) => index <= replay!.cursor && record.source === full.source && record.epoch === full.epoch
    && 'spanId' in record && record.spanId === full.spanId ? [{ record, index }] : [])
  return { span, records, channel: full.track.endsWith('/llm') ? { kind: 'model', source: full.source, epoch: full.epoch, spanId: full.spanId } : undefined }
}

function renderInspector() {
  if (!recording || !replay) return
  detailGeneration++
  const generation = detailGeneration
  for (const url of urls) URL.revokeObjectURL(url)
  urls = []
  const context = recordsForSelection()
  const full = selection?.kind === 'span' ? recording.full.timelineView().spans.get(selection.key) : undefined
  const currentRecord = context.records.at(-1)?.record
  const state = full ? spanState(context.span) : { text: context.records.length ? 'Recorded' : 'Not reached', css: '' }
  const title = full?.name ?? (selection?.kind === 'acp' ? 'External mayor output' : currentRecord ? recordTitle(currentRecord) : 'Select a span or event')
  const ids = context.span?.ids ?? currentRecord?.ids ?? {}
  const meta = full ? `${full.track} · ${full.spanId}` : currentRecord ? `${currentRecord.type} · #${currentRecord.seq}` : ''
  $('inspector-header').innerHTML = `<div class="inspect-title"><h2 id="inspector-heading">${escape(title)}</h2><span class="state-pill ${state.css}">${state.text}</span></div><div class="inspect-meta"><span>${escape(meta)}</span>${ids.agentSession ? `<span>session ${escape(String(ids.agentSession).slice(0, 16))}</span>` : ''}</div>`
  $('inspector-body').setAttribute('aria-labelledby', `tab-${tab}`)
  const attrs = context.span?.attrs ?? currentRecord?.attrs ?? {}
  const jumps = full ? `<div class="jump-actions"><button data-jump="begin">↤ Span start</button><button data-jump="end">Span end ↦</button></div>` : ''
  let html = jumps
  if (!context.records.length) html += '<p class="empty-detail">This work has not started at the current replay cursor. Step forward or seek to its start.</p>'
  else if (tab === 'replay' && context.channel) {
    const output = replay.output(context.channel)
    html += `<div class="output-header"><span>OBSERVED OUTPUT AT CURSOR</span><strong>${output.chunks} recorded ${output.chunks === 1 ? 'update' : 'updates'}</strong></div>`
    if (context.channel.kind === 'acp') html += '<p class="detail-note">External protocol output. The external mayor’s private model calls are not exposed.</p>'
    const completedResponse = !output.chunks && context.span?.end !== undefined ? attrs['gen_ai.output.messages'] : undefined
    if (completedResponse !== undefined) html += section('Completed response · no streaming updates recorded', completedResponse, true)
    else html += `<div class="output-text">${escape(output.text)}</div>`
    if (!output.chunks && completedResponse === undefined) html += '<p class="detail-note">No streamed updates have been recorded here at this cursor. A completed non-streaming response appears only when its end is reached.</p>'
    if (output.reasoning) html += `<details class="evidence-section replay-reasoning"><summary>Reasoning returned by the model</summary><div><pre>${escape(output.reasoning)}</pre></div></details>`
    html += section('Recorded update details', output.updates.map(update => ({ index: update.index, data: update.data })))
  }
  else if (tab === 'raw') {
    html += `<p class="raw-count">${context.records.length} records observed by this cursor. Asset references expand from the selected folder.</p>`
    for (const { record, index } of context.records) html += section(`#${record.seq} · ${seconds(timeOf(index))} · ${record.type}${record.type === 'instant' ? ` · ${record.name}` : ''}`, record)
  }
  else if (tab === 'evidence' && context.span) {
    html += section('Instructions', attrs['gen_ai.system_instructions'], true)
    html += section('Input messages', attrs['gen_ai.input.messages'], true)
    html += section('Tool catalog', attrs['gen_ai.tool.definitions'])
    html += section('Output messages', attrs['gen_ai.output.messages'], true)
    html += section('Tool arguments', attrs['gen_ai.tool.call.arguments'], true)
    html += section('Tool result', attrs['gen_ai.tool.call.result'], true)
    const usage = Object.fromEntries(Object.entries(attrs).filter(([key]) => key.startsWith('gen_ai.usage.') || key.startsWith('gen_ai.request.') || key.startsWith('gen_ai.response.')))
    if (Object.keys(usage).length) html += section('Model and usage', usage, true)
    for (const { record } of context.records.filter(({ record }) => record.type === 'instant' && record.name !== 'model.output.delta'))
      html += section(recordTitle(record), payload(recording, record), record.type === 'instant' && record.name === 'city.tool.result')
    html += section('All span attributes and links', { ids: context.span.ids, attrs, links: context.span.links })
  }
  else {
    const relevant = tab === 'replay' && context.span
      ? context.records.filter(({ record }) => record.type === 'instant' || record.type === 'log') : context.records
    for (const { record } of relevant) {
      const data = payload(recording, record)
      html += section(recordTitle(record), Object.keys(data).length ? data : record, true)
    }
    if (!relevant.length) html += '<p class="empty-detail">This span has no text output at this cursor. Full evidence contains its captured attributes, request, or result.</p>'
  }
  $('inspector-body').innerHTML = html
  const values = context.span ? [context.span.attrs, ...context.records.map(({ record }) => record)] : context.records.map(({ record }) => record)
  void renderAttachments(values, generation).catch(error => {
    if (generation === detailGeneration) $('inspector-body').append(Object.assign(document.createElement('p'), { className: 'detail-note', textContent: `Attachment unavailable: ${error.message}` }))
  })
}

function renderSummary() {
  if (!recording) return
  const spans = [...recording.full.timelineView().spans.values()]
  const model = spans.filter(span => span.track.endsWith('/llm'))
  const tools = spans.filter(span => span.track.endsWith('/tools'))
  const sessions = new Set<string>()
  for (const record of recording.records) {
    const nativeSession = record.ids?.agentSession
    const protocolSession = payload(recording, record).agentSession
    if (nativeSession !== undefined) sessions.add(String(nativeSession))
    if (typeof protocolSession === 'string') sessions.add(protocolSession)
  }
  const duration = recording.records.reduce((longest, _, index) => Math.max(longest, timeOf(index)), 0)
  $('stats').innerHTML = `<span><strong>${seconds(duration)}</strong> duration</span><span><strong>${sessions.size}</strong> agent ${sessions.size === 1 ? 'session' : 'sessions'}</span><span><strong>${model.length}</strong> model calls</span><span><strong>${tools.length}</strong> tool calls</span><span><strong>${recording.events.length}</strong> events</span>`
  $('integrity').textContent = recording.issues.length ? `Incomplete · ${recording.issues.length} ${recording.issues.length === 1 ? 'issue' : 'issues'}` : '✓ Recording complete'
  $('integrity').classList.toggle('warning', recording.issues.length > 0)
  $('issues').hidden = !recording.issues.length
  $('issue-summary').textContent = `${recording.issues.length} validation ${recording.issues.length === 1 ? 'issue' : 'issues'} · recorded evidence remains available`
  $('issue-list').innerHTML = recording.issues.map(issue => `<li>${escape(issue.message)}</li>`).join('')
  $('end-time').textContent = seconds(duration)
}

export async function openRecording(files: readonly LocalFile[]) {
  stop()
  $('import-status').className = ''
  $('import-status').textContent = 'Reading and validating local evidence…'
  try {
    const loaded = await loadRecording(files)
    recording = loaded
    replay = new Replay(loaded)
    const models = [...loaded.full.timelineView().spans.values()].filter(span => span.track.endsWith('/llm'))
    const candidate = models.at(-1) ?? [...loaded.full.timelineView().spans.values()][0]
    const acp = loaded.records.find(record => record.type === 'instant' && record.name === 'acp.output.delta')
    selection = candidate ? { kind: 'span', key: candidate.key } : acp
      ? { kind: 'acp', agentSession: String(payload(loaded, acp).agentSession ?? '') } : { kind: 'record', index: loaded.events[0] }
    $('recording-title').textContent = loaded.name
    $('empty-state').hidden = true
    $('workspace').hidden = false
    $('import-status').textContent = ''
    $<HTMLInputElement>('seek').max = String(loaded.events.length - 1)
    $<HTMLInputElement>('event-search').value = ''
    renderSummary()
    eventPosition = loaded.events.length - 1
    filterEvents()
    seek(eventPosition, false)
  }
  catch (error) {
    $('import-status').className = 'error'
    $('import-status').textContent = `Could not open recording: ${(error as Error).message}`
  }
}

function selectedFiles(files: FileList | null) {
  if (!files?.length) return
  void openRecording([...files].map(file => ({ path: file.webkitRelativePath || file.name, size: file.size,
    read: async () => new Uint8Array(await file.arrayBuffer()) })))
}

document.querySelectorAll('.open-folder').forEach(button => button.addEventListener('click', () => $<HTMLInputElement>('folder-input').click()))
$('open-records').addEventListener('click', () => $<HTMLInputElement>('record-input').click())
$('folder-input').addEventListener('change', event => selectedFiles((event.target as HTMLInputElement).files))
$('record-input').addEventListener('change', event => selectedFiles((event.target as HTMLInputElement).files))
$('play').addEventListener('click', play)
$('first').addEventListener('click', () => { stop(); seek(0) })
$('last').addEventListener('click', () => { stop(); seek(recording!.events.length - 1) })
$('previous').addEventListener('click', () => { stop(); seek(eventPosition - 1) })
$('next').addEventListener('click', () => { stop(); seek(eventPosition + 1) })
$('seek').addEventListener('input', event => { stop(); seek(Number((event.target as HTMLInputElement).value)) })
$('event-search').addEventListener('input', filterEvents)
$('events-earlier').addEventListener('click', () => { eventPage--; renderEvents() })
$('events-later').addEventListener('click', () => { eventPage++; renderEvents() })
$('event-list').addEventListener('click', event => {
  const row = (event.target as Element).closest<HTMLElement>('[data-event]')
  if (row) selectEvent(Number(row.dataset.event))
})
function timelineSelect(event: Event) {
  const target = (event.target as Element).closest<SVGElement>('[data-span],[data-event]')
  if (!target) return
  if (target.dataset.event) { selectEvent(Number(target.dataset.event)); return }
  selection = { kind: 'span', key: target.dataset.span! }
  renderTimeline()
  renderInspector()
}
$('timeline').addEventListener('click', timelineSelect)
$('timeline').addEventListener('keydown', event => { if (event.key === 'Enter') timelineSelect(event) })
document.querySelectorAll<HTMLButtonElement>('[data-tab]').forEach(button => button.addEventListener('click', () => {
  tab = button.dataset.tab as Tab
  document.querySelectorAll('[data-tab]').forEach(candidate => candidate.setAttribute('aria-selected', String(candidate === button)))
  renderInspector()
}))
$('inspector-body').addEventListener('click', event => {
  const button = (event.target as Element).closest<HTMLElement>('[data-jump]')
  if (!button || selection?.kind !== 'span' || !recording) return
  const span = recording.full.timelineView().spans.get(selection.key)!
  const indices = recording.events.flatMap((index, position) => {
    const record = recording!.records[index]
    return 'spanId' in record && record.spanId === span.spanId && record.source === span.source && record.epoch === span.epoch ? [position] : []
  })
  stop()
  seek(button.dataset.jump === 'begin' ? indices[0] : indices.at(-1)!)
})
document.addEventListener('keydown', event => {
  if (!recording || (event.target as Element).matches('input,textarea,select,button,[role=tab]')) return
  if (event.code === 'Space') { event.preventDefault(); play() }
  if (event.key === 'ArrowRight') { event.preventDefault(); stop(); seek(eventPosition + 1) }
  if (event.key === 'ArrowLeft') { event.preventDefault(); stop(); seek(eventPosition - 1) }
})

declare global { interface Window { airimayorTraceViewer: { open: typeof openRecording; seek: typeof seek; state: () => { cursor: number; eventPosition: number; playing: boolean; issues: number } } } }
window.airimayorTraceViewer = { open: openRecording, seek, state: () => ({ cursor: replay?.cursor ?? -1, eventPosition, playing, issues: recording?.issues.length ?? 0 }) }
