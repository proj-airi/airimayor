import type { TraceAssetRef } from '@proj-airi/trace-kit'
import type { LocalFile, LoadProgress, OutputChannel, Recording, Span } from './viewer-model'
import { spanKeyOf } from '@proj-airi/trace-kit'
import { recordingDirectory } from './recording-files'
import { isAsset, isObject, loadDirectory, loadRecording, payload, Replay } from './viewer-model'

export type Selection =
  | { kind: 'span'; key: string }
  | { kind: 'record'; index: number }
  | { kind: 'acp'; agentSession: string }
export interface SelectedFile {
  path: string
  file: Blob
}
export interface EventRow {
  position: number
  index: number
  time: number
  title: string
  agent: string
  kind: string
  selection?: Selection
}
export interface TimelineGroup {
  id: string
  label: string
  parent?: string
  kind: string
  expanded: boolean
}
export interface TimelineItem {
  id: string
  group: string
  label: string
  start: number
  end: number
  kind: string
  status: string
  position: number
  selection: Selection
}
export interface SessionSummary {
  name: string
  records: number
  spans: number
  models: number
  tools: number
  duration: number
  issues: string[]
  events: EventRow[]
  groups: TimelineGroup[]
  items: TimelineItem[]
  playTimes: number[]
  seekTimes: number[]
  seekPositions: number[]
}
export interface Frame {
  position: number
  index: number
  time: number
  selection?: Selection
  heading: string
  breadcrumb: string[]
  state: string
  channel: boolean
  text: string
  reasoning: string
  chunks: number
  completedOutput?: unknown
}
export interface EvidenceSection {
  title: string
  data: unknown
}
export interface Attachment {
  name: string
  type: string
  blob: Blob
}
export interface Evidence {
  sections: EvidenceSection[]
  attachments: Attachment[]
  rawIndices: number[]
}
type Progress = (update: LoadProgress) => void | Promise<void>

const names: Record<string, string> = {
  'source.started': 'Recording started',
  'source.stopped': 'Recording closed',
  'input.received': 'Player input received',
  'input.queued': 'Player input queued',
  'input.applied': 'Input sent to model',
  'stop.requested': 'Stop requested',
  'stop.applied': 'Stop applied',
  'plan.changed': 'Plan updated',
  'compaction.started': 'Compaction started',
  'compaction.finished': 'Compaction finished',
  'history.repaired': 'History repaired',
  'city.tool.result': 'City tool result',
  'recording.gap': 'Known recording gap',
  'session.fact': 'Session facts',
  'model.output.delta': 'Model output update',
  'acp.output.delta': 'External output update',
}

export function spanKind(span: Pick<Span, 'track'>) {
  return span.track.endsWith('/llm') ? 'model' : span.track.endsWith('/tools') ? 'tool' : 'run'
}

export class ViewerSession {
  private recording?: Recording
  private replay?: Replay
  private summary?: SessionSummary
  private spanLabels = new Map<string, string>()
  private positions = new Map<number, number>()

  async openFiles(files: SelectedFile[], progress: Progress) {
    return this.loaded(
      await loadRecording(
        files.map(
          ({ path, file }): LocalFile => ({
            path,
            size: file.size,
            read: async () => new Uint8Array(await file.arrayBuffer()),
          }),
        ),
        progress,
      ),
    )
  }

  async openDirectory(directory: Parameters<typeof recordingDirectory>[0], progress: Progress) {
    return this.loaded(await loadDirectory(recordingDirectory(directory), progress))
  }

  private loaded(recording: Recording): SessionSummary {
    this.recording = recording
    this.replay = new Replay(recording)
    this.spanLabels.clear()
    this.positions = new Map(recording.events.map((index, position) => [index, position]))
    const spans = [...recording.full.timelineView().spans.values()]
    const models = spans.filter((span) => spanKind(span) === 'model')
    const tools = spans.filter((span) => spanKind(span) === 'tool')
    const runs = spans.filter((span) => spanKind(span) === 'run' && span.name !== 'chat-turn')
    const turns = spans.filter((span) => span.name === 'chat-turn')
    const runKeys = new Set(runs.map((span) => span.key))
    const runFor = (span: Span) => {
      const seen = new Set<string>()
      let parent: Span | undefined = span
      while (parent && !seen.has(parent.key)) {
        if (runKeys.has(parent.key)) return parent
        seen.add(parent.key)
        parent = parent.parentKey ? recording.full.timelineView().spans.get(parent.parentKey) : undefined
      }
      return runs.find((run) => run.ids?.run !== undefined && run.ids.run === span.ids?.run)
    }
    const runOwners = new Map(spans.map((span) => [span.key, runFor(span)]))
    const origin = recording.full.reader.timeOf(recording.records[0]).time
    const duration = recording.records.reduce(
      (end, record) => Math.max(end, recording.full.reader.timeOf(record).time - origin),
      0,
    )
    models.forEach((span, index) =>
      this.spanLabels.set(span.key, `Request ${index + 1} · ${span.attrs['gen_ai.request.model'] ?? span.name}`),
    )
    tools.forEach((span) =>
      this.spanLabels.set(
        span.key,
        String(span.attrs['gen_ai.tool.name'] ?? span.name.replace(/^execute_tool\s*/, '')),
      ),
    )
    runs.forEach((span, index) => this.spanLabels.set(span.key, `Run ${index + 1} · ${span.ids?.agent ?? 'Mayor'}`))
    turns.forEach((span, index) => this.spanLabels.set(span.key, `Turn ${index + 1}`))
    const groups: TimelineGroup[] = []
    const items: TimelineItem[] = []
    const owner = new Map<string, string>()
    turns.forEach((turn, index) => {
      const id = `turn:${turn.key}`
      groups.push({
        id,
        label: `Turn ${index + 1} · ${turn.status ?? 'incomplete'}`,
        kind: 'turn',
        expanded: index === turns.length - 1,
      })
      owner.set(String(turn.ids?.turn ?? turn.spanId), id)
    })
    const ensureRun = (span: Span) => {
      const id = `run:${span.ids?.run ?? span.key}`
      if (groups.some((group) => group.id === id)) return id
      const parent = owner.get(String(span.ids?.turn))
      const count = (kind: string) =>
        spans.filter((child) => runOwners.get(child.key)?.key === span.key && spanKind(child) === kind).length
      groups.push({
        id,
        parent,
        label: `${this.spanLabels.get(span.key) ?? 'Agent run'} · ${((span.end ?? origin + duration) - span.start).toFixed(0)} ms`,
        kind: 'run',
        expanded: true,
      })
      groups.push({
        id: `${id}:model`,
        parent: id,
        label: `Model requests · ${count('model')}`,
        kind: 'model',
        expanded: true,
      })
      groups.push({
        id: `${id}:tool`,
        parent: id,
        label: `City tools · ${count('tool')}`,
        kind: 'tool',
        expanded: true,
      })
      return id
    }
    runs.forEach(ensureRun)
    for (const span of spans) {
      if (span.name === 'chat-turn') continue
      const kind = spanKind(span)
      const run = runOwners.get(span.key)
      let group: string
      if (run) group = ensureRun(run)
      else {
        group = 'unscoped'
        if (!groups.some((item) => item.id === group)) {
          groups.push({ id: group, label: 'Calls without a recorded run', kind: 'run', expanded: true })
          for (const kind of ['model', 'tool'])
            groups.push({
              id: `${group}:${kind}`,
              parent: group,
              label: kind === 'model' ? 'Model requests' : 'City tools',
              kind,
              expanded: true,
            })
        }
      }
      const first = recording.index.spans.get(span.key)?.[0] ?? 0
      items.push({
        id: span.key,
        group: kind === 'run' ? group : `${group}:${kind}`,
        label: this.spanLabels.get(span.key) ?? span.name,
        start: span.start - origin,
        end: (span.end ?? origin + duration) - origin,
        kind,
        status: span.status ?? 'incomplete',
        position: this.positions.get(first) ?? 0,
        selection: { kind: 'span', key: span.key },
      })
    }
    const events = recording.events.map((index, position): EventRow => {
      const record = recording.records[index]
      let title = names[record.type] ?? record.type
      if (record.type === 'span.begin' || record.type === 'span.complete')
        title = this.spanLabels.get(spanKeyOf(record.source, record.epoch, record.spanId)) ?? record.name
      else if (record.type === 'span.end') title = `Finished · ${record.status}`
      else if (record.type === 'span.update') title = 'Input / attributes captured'
      else if (record.type === 'log') title = `${record.level} · ${record.message}`
      else if (record.type === 'instant') {
        const data = payload(recording, record)
        title = `${names[record.name] ?? record.name}${typeof data.index === 'number' ? ` #${data.index + 1}` : ''}`
      }
      return {
        position,
        index,
        time: recording.full.reader.timeOf(record).time - origin,
        title,
        agent: String(record.ids?.agent ?? ('track' in record ? (record.track.split('/')[1] ?? 'City') : 'City')),
        kind: 'track' in record ? spanKind(record) : 'event',
        selection: this.selectionAt(index),
      }
    })
    for (const [session, indices] of recording.index.sessions) {
      if (
        !indices.some(
          (index) =>
            recording.records[index].type === 'instant' &&
            (recording.records[index] as { name?: string }).name === 'acp.output.delta',
        )
      )
        continue
      const id = `acp:${session}`
      groups.push({ id, label: `External conversation · ${session.slice(0, 12)}`, kind: 'run', expanded: true })
      const first = this.positions.get(indices[0]) ?? 0,
        last = this.positions.get(indices.at(-1)!) ?? first
      items.push({
        id,
        group: id,
        label: 'External mayor output',
        start: events[first].time,
        end: events[last].time,
        kind: 'run',
        status: 'external',
        position: first,
        selection: { kind: 'acp', agentSession: session },
      })
    }
    const playTimes = events.map(() => 0)
    for (let index = 1; index < events.length; index++)
      playTimes[index] = playTimes[index - 1] + Math.min(1500, Math.max(0, events[index].time - events[index - 1].time))
    const ordered = [...events].sort((a, b) => a.time - b.time || a.position - b.position)
    return (this.summary = {
      name: recording.name,
      records: recording.records.length,
      spans: spans.length,
      models: models.length,
      tools: tools.length,
      duration,
      issues: recording.issues.map((issue) => issue.message),
      events,
      groups,
      items,
      playTimes,
      seekTimes: ordered.map((row) => row.time),
      seekPositions: ordered.map((row) => row.position),
    })
  }

  private selectionAt(index: number): Selection | undefined {
    const recording = this.recording!
    const record = recording.records[index]
    if (record.type === 'instant' && record.name === 'acp.output.delta')
      return {
        kind: 'acp',
        agentSession: String(payload(recording, record).agentSession ?? record.ids?.agentSession ?? ''),
      }
    if ('spanId' in record && record.spanId) {
      const key = spanKeyOf(record.source, record.epoch, record.spanId)
      if (recording.full.timelineView().spans.has(key)) return { kind: 'span', key }
    }
    if (record.type === 'instant' || record.type === 'log') return { kind: 'record', index }
  }

  private context(selection: Selection | undefined, cursor: number) {
    const recording = this.recording!
    let indices: number[] = [],
      channel: OutputChannel | undefined,
      span: Span | undefined
    if (selection?.kind === 'span') {
      span = recording.full.timelineView().spans.get(selection.key)
      indices = recording.index.through(recording.index.spans.get(selection.key), cursor)
      if (span?.track.endsWith('/llm'))
        channel = { kind: 'model', source: span.source, epoch: span.epoch, spanId: span.spanId }
    } else if (selection?.kind === 'acp') {
      indices = recording.index.through(recording.index.sessions.get(selection.agentSession), cursor)
      channel = { kind: 'acp', agentSession: selection.agentSession }
    } else if (selection?.kind === 'record' && selection.index <= cursor) indices = [selection.index]
    return { indices, channel, span }
  }

  frame(position: number, selection?: Selection, follow = true): Frame {
    const recording = this.recording!,
      replay = this.replay!,
      summary = this.summary!
    position = Math.max(0, Math.min(summary.events.length - 1, Math.floor(position)))
    const row = summary.events[position]
    replay.seek(row.index)
    const candidate = row.selection
    if (
      follow &&
      candidate &&
      !(candidate.kind === 'span' && spanKind(recording.full.timelineView().spans.get(candidate.key)!) === 'run')
    )
      selection = candidate
    const { indices, channel, span } = this.context(selection, row.index)
    const current = span ? replay.view.timelineView().spans.get(span.key) : undefined
    const state = span
      ? !current
        ? 'Not reached'
        : current.end === undefined
          ? position === summary.events.length - 1
            ? 'Incomplete'
            : 'In progress'
          : (current.status ?? 'Completed')
      : indices.length
        ? 'Recorded'
        : 'Not reached'
    const output = channel ? replay.output(channel) : { text: '', reasoning: '', chunks: 0 }
    const breadcrumb: string[] = []
    let parent = span
    const seen = new Set<string>()
    while (parent && !seen.has(parent.key)) {
      seen.add(parent.key)
      breadcrumb.unshift(this.spanLabels.get(parent.key) ?? parent.name)
      parent = parent.parentKey ? recording.full.timelineView().spans.get(parent.parentKey) : undefined
    }
    if (span?.ids?.turn) {
      const turn = [...recording.full.timelineView().spans.values()].find(
        (item) => item.name === 'chat-turn' && item.ids?.turn === span.ids?.turn,
      )
      if (turn && !seen.has(turn.key)) breadcrumb.unshift(this.spanLabels.get(turn.key)!)
    }
    return {
      position,
      index: row.index,
      time: row.time,
      selection,
      heading: span
        ? (this.spanLabels.get(span.key) ?? span.name)
        : selection?.kind === 'acp'
          ? 'External mayor output'
          : selection?.kind === 'record'
            ? summary.events[this.positions.get(selection.index) ?? 0].title
            : 'Select a call or event',
      breadcrumb,
      state,
      channel: !!channel,
      text: output.text,
      reasoning: output.reasoning,
      chunks: output.chunks,
      completedOutput:
        channel && !output.chunks && current?.end !== undefined
          ? recording.assets.resolve(current.attrs['gen_ai.output.messages'])
          : undefined,
    }
  }

  async evidence(selection: Selection | undefined, position: number, raw = false): Promise<Evidence> {
    const recording = this.recording!,
      cursor = this.summary!.events[position].index
    const context = this.context(selection, cursor)
    if (raw) return { sections: [], attachments: [], rawIndices: context.indices }
    this.replay!.seek(cursor)
    const current = context.span ? this.replay!.view.timelineView().spans.get(context.span.key) : undefined
    const sections: EvidenceSection[] = []
    const add = (title: string, data: unknown) => {
      if (data !== undefined) sections.push({ title, data: recording.assets.resolve(data) })
    }
    if (current) {
      for (const [title, key] of [
        ['Instructions', 'gen_ai.system_instructions'],
        ['Input messages', 'gen_ai.input.messages'],
        ['Tool catalog', 'gen_ai.tool.definitions'],
        ['Output messages', 'gen_ai.output.messages'],
        ['Tool arguments', 'gen_ai.tool.call.arguments'],
        ['Tool result', 'gen_ai.tool.call.result'],
      ])
        add(title, current.attrs[key])
      add('Attributes and causal links', { ids: current.ids, attrs: current.attrs, links: current.links })
    }
    for (const index of context.indices) {
      const record = recording.records[index]
      if (record.type === 'instant' && record.name !== 'model.output.delta' && record.name !== 'acp.output.delta')
        add(names[record.name] ?? record.name, payload(recording, record))
      if (record.type === 'log') add(`${record.level} · ${record.message}`, record)
    }
    if (!sections.length && context.channel)
      add('Recorded updates at cursor', this.replay!.output(context.channel).updates)
    const refs = new Map<string, TraceAssetRef>()
    const visit = (value: unknown) => {
      if (isAsset(value)) {
        const key = `${value.$asset}\0${value.mediaType}`
        if (refs.has(key)) return
        refs.set(key, value)
        visit(recording.assets.data(value))
      } else if (Array.isArray(value)) value.forEach(visit)
      else if (isObject(value)) Object.values(value).forEach(visit)
    }
    visit(current?.attrs)
    context.indices.forEach((index) => visit(recording.records[index]))
    const attachments: Attachment[] = []
    for (const ref of refs.values()) {
      if (ref.mediaType === 'application/json') continue
      const file = await recording.assets.file(ref)
      if (file)
        attachments.push({
          name: ref.$asset.split('/').at(-1)!,
          type: ref.mediaType,
          blob: new Blob([(await file.read()) as Uint8Array<ArrayBuffer>], { type: ref.mediaType }),
        })
    }
    return { sections, attachments, rawIndices: context.indices }
  }

  rawRecords(indices: number[]) {
    return indices.map((index) => this.recording!.records[index])
  }

  searchEvents(query: string) {
    query = query.trim().toLowerCase()
    return this.summary!.events.filter(
      (row) => !query || `${row.title} ${row.agent} ${row.kind}`.toLowerCase().includes(query),
    ).map((row) => row.position)
  }
}
