import type { TraceAssetRef, TraceRecord } from '@proj-airi/trace-kit'
import type { EventLogState } from '@proj-airi/trace-kit/projections'
import type { TraceItemRef } from '@proj-airi/trace-kit/views'
import type { LocalFile, LoadProgress, OutputChannel, Recording, Span } from './viewer-model'
import { recordKey, spanKeyOf } from '@proj-airi/trace-kit'
import { createEventLogProjection, TraceReader } from '@proj-airi/trace-kit/projections'
import { itemOfRecord } from '@proj-airi/trace-kit/views'
import { recordingDirectory } from './recording-files'
import { isOverviewRecord } from './session-view'
import { isAsset, isObject, loadDirectory, loadRecording, payload, Replay } from './viewer-model'
import { RecordingAnalysis, type AnalysisFilter, type SearchQuery } from './recording-analysis'
import { CityRecording, type CityQuery } from './city-view'
import { RecordingScope, presentRecords, type RecordedTurn } from './recording-scope'

export type Selection =
  | { kind: 'span'; key: string }
  | { kind: 'record'; index: number }
  | { kind: 'acp'; agentSession: string }
export interface SelectedFile {
  path: string
  file: Blob
}
export interface SessionSummary {
  name: string
  records: number
  spans: number
  models: number
  tools: number
  duration: number
  origin: number
  clock: string
  issues: string[]
  events: { index: number; time: number }[]
  overview: TraceRecord[]
  defaultSelection?: Selection
  seekTimes: number[]
  seekPositions: number[]
  turns: RecordedTurn[]
}
export interface Frame {
  position: number
  index: number
  time: number
  selection?: Selection
  item?: TraceItemRef
  heading: string
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

export class ViewerSession {
  private recording?: Recording
  private replay?: Replay
  private summary?: SessionSummary
  private positions = new Map<number, number>()
  private recordKeys = new Map<string, number>()
  private structural: number[] = []
  private analysis?: RecordingAnalysis
  private cityRecording?: CityRecording
  private scope?: RecordingScope

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
    this.analysis = new RecordingAnalysis(recording)
    this.cityRecording = new CityRecording(recording)
    this.replay = new Replay(recording)
    this.scope = new RecordingScope(recording)
    this.positions = new Map(recording.events.map((index, position) => [index, position]))
    this.recordKeys = new Map(recording.records.map((record, index) => [recordKey(record), index]))
    this.structural = recording.records.flatMap((record, index) => (isOverviewRecord(record) ? [index] : []))
    const spans = [...recording.full.timelineView().spans.values()]
    const models = spans.filter((span) => span.track.endsWith('/llm'))
    const origin = recording.full.reader.timeOf(recording.records[0]).time
    const events = recording.events.map((index) => ({
      index,
      time: recording.full.reader.timeOf(recording.records[index]).time - origin,
    }))
    const duration = events.reduce((end, event) => Math.max(end, event.time), 0)
    const ordered = events
      .map((event, position) => ({ ...event, position }))
      .sort((a, b) => a.time - b.time || a.position - b.position)
    const lastModel = models.at(-1)
    const lastOutput = [...recording.index.outputs.values()].at(-1)?.at(-1)
    return (this.summary = {
      name: recording.name,
      records: recording.records.length,
      spans: spans.length,
      models: models.length,
      tools: spans.filter((span) => span.track.endsWith('/tools')).length,
      duration,
      origin,
      clock: recording.records[0].clock,
      issues: recording.issues.map((issue) => issue.message),
      events,
      overview: this.overview(),
      turns: this.scope.turns,
      defaultSelection: lastModel
        ? { kind: 'span', key: lastModel.key }
        : lastOutput === undefined
          ? undefined
          : this.selectionAt(lastOutput),
      seekTimes: ordered.map((event) => event.time),
      seekPositions: ordered.map((event) => event.position),
    })
  }

  overview(turn?: string) {
    return presentRecords(this.scope!.records(turn).filter(isOverviewRecord))
  }

  locate(item: TraceItemRef) {
    const index =
      item.kind === 'span' ? this.recording!.index.spans.get(item.key)?.[0] : this.recordKeys.get(item.key)
    if (index === undefined) throw new Error('The selected item is absent from this recording.')
    return { position: this.positions.get(index) ?? 0, selection: this.selectionAt(index) }
  }

  snapshot(position: number) {
    const cursor = this.summary!.events[position].index
    return presentRecords(
      this.recording!.index.through(this.structural, cursor).map((index) => this.recording!.records[index]),
    )
  }

  eventLog(turn?: string): EventLogState {
    const projection = createEventLogProjection({ limit: Infinity })
    const reader = new TraceReader([projection], this.summary!.clock)
    reader.handleRecords(this.scope!.records(turn))
    return reader.view(projection)
  }

  analyze(filter: AnalysisFilter = {}, offset = 0) {
    return this.analysis!.analyze(filter, offset)
  }

  search(query: SearchQuery) {
    return this.analysis!.search(query)
  }

  city(position: number, query: CityQuery = {}) {
    return this.cityRecording!.view(this.summary!.events[position].index, query)
  }

  async cityImage(index: number) {
    const files = payload(this.recording!, this.recording!.records[index]).files
    if (!Array.isArray(files)) return
    for (const file of files)
      if (isObject(file) && isAsset(file.asset) && file.asset.mediaType.startsWith('image/'))
        return this.attachment(file.asset)
  }

  private async attachment(ref: TraceAssetRef): Promise<Attachment | undefined> {
    const file = await this.recording!.assets.file(ref)
    if (file)
      return {
        name: ref.$asset.split('/').at(-1)!,
        type: ref.mediaType,
        blob: new Blob([(await file.read()) as Uint8Array<ArrayBuffer>], { type: ref.mediaType }),
      }
  }

  private selectionAt(index: number): Selection | undefined {
    const recording = this.recording!,
      record = recording.records[index]
    if (record.type === 'instant' && record.name === 'acp.output.delta')
      return {
        kind: 'acp',
        agentSession: String(payload(recording, record).agentSession ?? record.ids?.agentSession ?? ''),
      }
    if ('spanId' in record && record.spanId) {
      const key = spanKeyOf(record.source, record.epoch, record.spanId)
      if (recording.full.timelineView().spans.has(key)) return { kind: 'span', key }
    }
    if (record.type === 'instant' || record.type === 'log' || record.type === 'state')
      return { kind: 'record', index }
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
    const candidate = this.selectionAt(row.index)
    if (
      follow &&
      candidate &&
      !(
        candidate.kind === 'span' &&
        !recording.full
          .timelineView()
          .spans.get(candidate.key)!
          .track.match(/\/(llm|tools)$/)
      )
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
    const item =
      selection?.kind === 'span'
        ? { kind: 'span' as const, key: selection.key }
        : selection?.kind === 'record'
          ? itemOfRecord(recording.records[selection.index])
          : undefined
    return {
      position,
      index: row.index,
      time: row.time,
      selection,
      item,
      heading: span?.name ?? (selection?.kind === 'acp' ? 'External mayor output' : 'Recorded event'),
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
      if (
        record.type === 'instant' &&
        record.name !== 'model.output.delta' &&
        record.name !== 'acp.output.delta'
      )
        add(record.name, payload(recording, record))
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
    context.indices.forEach((index) => {
      const record = recording.records[index]
      visit(record)
      if (typeof record.attrs?.['airimayor.data'] === 'string') visit(payload(recording, record))
    })
    const attachments: Attachment[] = []
    for (const ref of refs.values()) {
      if (ref.mediaType === 'application/json') continue
      const attachment = await this.attachment(ref)
      if (attachment) attachments.push(attachment)
    }
    return { sections, attachments, rawIndices: context.indices }
  }

  rawRecords(indices: number[]) {
    return indices.map((index) => this.recording!.records[index])
  }
}
