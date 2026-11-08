import type { TraceAssetRef, TraceRecord } from '@proj-airi/trace-kit'
import { TRACE_FORMAT, traceRecordSchema } from '@proj-airi/trace-kit'
import { createTraceViewReader } from '@proj-airi/trace-kit/views'
import { safeParse } from 'valibot'

export interface LocalFile {
  path: string
  size: number
  read: () => Promise<Uint8Array>
}

export interface Issue { message: string; index?: number }
export type ViewReader = ReturnType<typeof createTraceViewReader>
export type Timeline = ReturnType<ViewReader['timelineView']>
export type Span = Timeline['spans'] extends Map<string, infer Value> ? Value : never

export function isObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

export function isAsset(value: unknown): value is TraceAssetRef {
  return isObject(value) && typeof value.$asset === 'string' && typeof value.mediaType === 'string'
    && typeof value.bytes === 'number' && Number.isSafeInteger(value.bytes) && value.bytes >= 0
}

export function safePath(path: string) {
  return path.length > 0 && !path.includes('\\') && !path.includes(':')
    && path.split('/').every(part => part.length > 0 && part !== '.' && part !== '..')
}

export class AssetStore {
  private json = new Map<string, unknown>()
  private validated = new Set<string>()
  readonly references = new Map<string, TraceAssetRef>()

  constructor(private files: Map<string, LocalFile>, readonly issues: Issue[]) {}

  data(ref: unknown): unknown {
    return isAsset(ref) ? this.json.get(`${ref.$asset}\0${ref.mediaType}`) : undefined
  }

  file(ref: TraceAssetRef): LocalFile | undefined {
    return safePath(ref.$asset) && ref.$asset.startsWith('assets/') ? this.files.get(ref.$asset) : undefined
  }

  async visit(value: unknown, index: number): Promise<void> {
    if (Array.isArray(value)) {
      for (const item of value) await this.visit(item, index)
      return
    }
    if (!isObject(value)) return
    if (!('$asset' in value)) {
      for (const item of Object.values(value)) await this.visit(item, index)
      return
    }
    if (!isAsset(value) || !safePath(value.$asset) || !value.$asset.startsWith('assets/')) {
      this.issues.push({ index, message: 'Unsafe or invalid asset reference' })
      return
    }
    const key = `${value.$asset}\0${value.mediaType}`
    const file = this.file(value)
    if (!file) {
      this.issues.push({ index, message: `Missing asset: ${value.$asset}` })
      return
    }
    if (file.size !== value.bytes) {
      this.issues.push({ index, message: `Asset byte count differs: ${value.$asset} (${file.size}, expected ${value.bytes})` })
      return
    }
    this.references.set(key, value)
    if (this.validated.has(key)) return
    this.validated.add(key)
    if (value.mediaType === 'application/json') {
      try {
        const data: unknown = JSON.parse(new TextDecoder().decode(await file.read()))
        this.json.set(key, data)
        await this.visit(data, index)
      }
      catch (error) { this.issues.push({ index, message: `Invalid JSON asset ${value.$asset}: ${(error as Error).message}` }) }
    }
  }

  resolve(value: unknown, seen = new Set<string>()): unknown {
    if (isAsset(value)) {
      const key = `${value.$asset}\0${value.mediaType}`
      const data = this.data(value)
      if (data === undefined || seen.has(key)) return value
      return this.resolve(data, new Set([...seen, key]))
    }
    if (Array.isArray(value)) return value.map(item => this.resolve(item, seen))
    if (isObject(value)) return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, this.resolve(item, seen)]))
    return value
  }
}

export interface Recording {
  name: string
  records: TraceRecord[]
  issues: Issue[]
  assets: AssetStore
  full: ViewReader
  events: number[]
}

function validateLifecycle(records: TraceRecord[], issues: Issue[]) {
  const epochs = new Map<string, { seq: number; stopped: boolean }>()
  const spans = new Map<string, boolean>()
  records.forEach((record, index) => {
    const epoch = `${record.source}/${record.epoch}`
    const previous = epochs.get(epoch)
    const issue = (message: string) => issues.push({ index, message })
    if (record.seq !== (previous ? previous.seq + 1 : 0)) issue(`Sequence gap or duplicate at ${epoch} #${record.seq}`)
    if (previous?.stopped) issue(`Record after source.stopped: ${epoch}`)
    if (record.type === 'source.started' ? previous !== undefined : previous === undefined)
      issue(record.type === 'source.started' ? `Repeated source.started: ${epoch}` : `Missing source.started: ${epoch}`)
    epochs.set(epoch, { seq: record.seq, stopped: previous?.stopped || record.type === 'source.stopped' })
    if (record.type === 'source.lost') issue(`Source lost: ${record.of.source}/${record.of.epoch}`)
    if (record.type === 'span.begin' || record.type === 'span.complete') {
      const key = `${epoch}/${record.spanId}`
      if (spans.has(key)) issue(`Repeated span: ${record.spanId}`)
      spans.set(key, record.type === 'span.begin')
    }
    else if (record.type === 'span.end' || record.type === 'span.update') {
      const key = `${epoch}/${record.spanId}`
      if (!spans.get(key)) issue(`Orphan ${record.type}: ${record.spanId}`)
      if (record.type === 'span.end') spans.set(key, false)
    }
  })
  for (const [epoch, state] of epochs)
    if (!state.stopped) issues.push({ message: `Incomplete source: ${epoch} has no source.stopped` })
  for (const [span, open] of spans)
    if (open) issues.push({ message: `Incomplete span: ${span} has no end` })
}

export async function loadRecording(selected: readonly LocalFile[]): Promise<Recording> {
  if (!selected.length) throw new Error('Select a recording folder containing records.jsonl and assets.')
  for (const file of selected)
    if (!safePath(file.path)) throw new Error(`Unsafe selected path: ${file.path}`)
  const recordFiles = selected.filter(file => file.path === 'records.jsonl' || file.path.endsWith('/records.jsonl'))
  if (recordFiles.length !== 1) throw new Error('Select exactly one session folder containing records.jsonl.')
  const recordFile = recordFiles[0]
  const prefix = recordFile.path.slice(0, -'records.jsonl'.length)
  const files = new Map<string, LocalFile>()
  for (const file of selected.filter(file => file.path.startsWith(prefix))) {
    const key = file.path.slice(prefix.length)
    if (files.has(key)) throw new Error(`Duplicate selected file: ${key}`)
    files.set(key, file)
  }
  const issues: Issue[] = []
  const records: TraceRecord[] = []
  const text = new TextDecoder().decode(await recordFile.read())
  for (const [line, source] of text.split(/\r?\n/).entries()) {
    if (!source.trim()) continue
    let value: unknown
    try { value = JSON.parse(source) }
    catch { issues.push({ message: `Line ${line + 1}: invalid or truncated JSON` }); continue }
    const parsed = safeParse(traceRecordSchema, value)
    if (!parsed.success) {
      issues.push({ message: `Line ${line + 1}: invalid native record (${parsed.issues[0].message})` })
      continue
    }
    if (parsed.output.type === 'source.started' && parsed.output.format !== TRACE_FORMAT)
      throw new Error(`Unsupported trace format ${parsed.output.format}; expected ${TRACE_FORMAT}.`)
    records.push(parsed.output)
  }
  if (!records.length) throw new Error('No native trace-kit records found. This viewer reads records.jsonl.')
  validateLifecycle(records, issues)
  const assets = new AssetStore(files, issues)
  for (const [index, record] of records.entries()) await assets.visit(record, index)
  const full = createTraceViewReader(records[0].clock, { seriesRetention: false })
  full.reader.handleRecords(records)
  const events = records.flatMap((record, index) => record.type === 'definition' || record.type === 'clock.snapshot' ? [] : [index])
  return { name: prefix.split('/').filter(Boolean).at(-1) ?? recordFile.path, records, issues, assets, full, events }
}

export function payload(recording: Recording, record: TraceRecord): Record<string, unknown> {
  const data = recording.assets.data(record.attrs?.['airimayor.data'])
  return isObject(data) ? data : {}
}

export type OutputChannel = { kind: 'model'; source: string; epoch: string; spanId: string }
  | { kind: 'acp'; agentSession: string }

export class Replay {
  readonly view: ViewReader
  cursor = -1

  constructor(readonly recording: Recording) {
    this.view = createTraceViewReader(recording.records[0].clock, { seriesRetention: false })
  }

  seek(index: number) {
    const next = Math.max(-1, Math.min(this.recording.records.length - 1, Math.floor(index)))
    if (next < this.cursor) { this.view.reader.reset(); this.cursor = -1 }
    this.view.reader.handleRecords(this.recording.records.slice(this.cursor + 1, next + 1))
    this.cursor = next
    return this.view.timelineView()
  }

  output(channel: OutputChannel) {
    let text = ''
    let reasoning = ''
    let chunks = 0
    const updates: { index: number; recordIndex: number; data: Record<string, unknown> }[] = []
    for (let index = 0; index <= this.cursor; index++) {
      const record = this.recording.records[index]
      if (record.type !== 'instant') continue
      const data = payload(this.recording, record)
      const matches = channel.kind === 'model'
        ? record.name === 'model.output.delta' && record.source === channel.source && record.epoch === channel.epoch && record.spanId === channel.spanId
        : record.name === 'acp.output.delta' && data.agentSession === channel.agentSession
      if (!matches) continue
      if (typeof data.text === 'string') text += data.text
      if (typeof data.reasoning === 'string') reasoning += data.reasoning
      chunks++
      updates.push({ index: typeof data.index === 'number' ? data.index : chunks - 1, recordIndex: index, data })
    }
    return { text, reasoning, chunks, updates }
  }
}
