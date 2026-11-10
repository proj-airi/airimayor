import type { TraceAssetRef, TraceRecord } from '@proj-airi/trace-kit'
import { spanKeyOf, TRACE_FORMAT, traceRecordSchema } from '@proj-airi/trace-kit'
import { sortedLastIndex } from 'es-toolkit/compat'
import { safeParse } from 'valibot'
import { createSessionViewReader } from './session-view'

export interface LocalFile {
  path: string
  size: number
  read: () => Promise<Uint8Array>
}

export interface LocalDirectory {
  name: string
  file: (path: string) => Promise<LocalFile | undefined>
}

export interface Issue { message: string; index?: number }
export interface LoadProgress { phase: 'read' | 'records' | 'assets' | 'timeline'; completed: number; total: number }
export type ViewReader = ReturnType<typeof createSessionViewReader>
export type Timeline = ReturnType<ViewReader['timelineView']>
export type Span = Timeline['spans'] extends Map<string, infer Value> ? Value : never

export function isObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

export function isAsset(value: unknown): value is TraceAssetRef {
  return isObject(value) && typeof value.$asset === 'string'
    && typeof value.mediaType === 'string' && value.mediaType.length > 0
    && typeof value.bytes === 'number' && Number.isSafeInteger(value.bytes) && value.bytes >= 0
}

export function safePath(path: string) {
  return path.length > 0 && !path.includes('\\') && !path.includes(':')
    && path.split('/').every(part => part.length > 0 && part !== '.' && part !== '..')
}

export class AssetStore {
  private json = new Map<string, unknown>()
  private inline = new Map<string, Record<string, unknown>>()
  private validated = new Set<string>()
  private files = new Map<string, Promise<LocalFile | undefined>>()
  readonly references = new Map<string, TraceAssetRef>()

  constructor(private directory: LocalDirectory, readonly issues: Issue[]) {}

  data(ref: unknown): unknown {
    if (typeof ref === 'string') return this.inline.get(ref)
    return isAsset(ref) ? this.json.get(`${ref.$asset}\0${ref.mediaType}`) : undefined
  }

  async visitRecord(record: TraceRecord, index: number): Promise<void> {
    if (record.type === 'instant' && (record.name === 'model.output.delta' || record.name === 'acp.output.delta')) {
      const encoded = record.attrs?.['airimayor.data']
      try {
        if (typeof encoded !== 'string') throw new Error('Output delta must contain inline JSON')
        if (!this.inline.has(encoded)) {
          const data: unknown = JSON.parse(encoded)
          if (!isObject(data)) throw new Error('Output delta must be a JSON object')
          this.inline.set(encoded, data)
        }
        await this.visit(this.inline.get(encoded), index)
      }
      catch (error) { this.issues.push({ index, message: `Invalid output delta: ${(error as Error).message}` }) }
    }
    await this.visit(record, index)
  }

  async file(ref: TraceAssetRef): Promise<LocalFile | undefined> {
    if (!safePath(ref.$asset) || !ref.$asset.startsWith('assets/')) return
    let file = this.files.get(ref.$asset)
    if (!file) {
      file = this.directory.file(ref.$asset)
      this.files.set(ref.$asset, file)
    }
    return file
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
    let file: LocalFile | undefined
    try { file = await this.file(value) }
    catch (error) {
      this.issues.push({ index, message: `${value.$asset}: ${(error as Error).message}` })
      return
    }
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
  index: RecordingIndex
}

export type OutputChannel = { kind: 'model'; source: string; epoch: string; spanId: string }
  | { kind: 'acp'; agentSession: string }

function channelKey(channel: OutputChannel) {
  return channel.kind === 'model' ? spanKeyOf(channel.source, channel.epoch, channel.spanId) : `acp:${channel.agentSession}`
}

export class RecordingIndex {
  readonly spans = new Map<string, number[]>()
  readonly sessions = new Map<string, number[]>()
  readonly outputs = new Map<string, number[]>()

  constructor(records: readonly TraceRecord[], assets: AssetStore) {
    const add = (map: Map<string, number[]>, key: string, index: number) => {
      let values = map.get(key)
      if (!values) { values = []; map.set(key, values) }
      values.push(index)
    }
    records.forEach((record, index) => {
      if ('spanId' in record && record.spanId) add(this.spans, spanKeyOf(record.source, record.epoch, record.spanId), index)
      const data = assets.data(record.attrs?.['airimayor.data'])
      const session = record.ids?.agentSession ?? (isObject(data) ? data.agentSession : undefined)
      if (session !== undefined) add(this.sessions, String(session), index)
      if (record.type !== 'instant') return
      if (record.name === 'model.output.delta' && record.spanId)
        add(this.outputs, channelKey({ kind: 'model', source: record.source, epoch: record.epoch, spanId: record.spanId }), index)
      else if (record.name === 'acp.output.delta' && session !== undefined)
        add(this.outputs, channelKey({ kind: 'acp', agentSession: String(session) }), index)
    })
  }

  through(indices: readonly number[] | undefined, cursor: number) {
    return indices ? indices.slice(0, sortedLastIndex(indices, cursor)) : []
  }
}

function validateLifecycle(records: TraceRecord[], issues: Issue[]): void {
  const epochs = new Map<string, { seq: number; stopped: boolean }>()
  const spans = new Map<string, { open: boolean; track: string }>()
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
      spans.set(key, { open: record.type === 'span.begin', track: record.track })
    }
    else if (record.type === 'span.end' || record.type === 'span.update') {
      const key = `${epoch}/${record.spanId}`
      const span = spans.get(key)
      if (!span?.open) issue(`Orphan ${record.type}: ${record.spanId}`)
      else {
        if (span.track !== record.track) issue(`Span changes track: ${record.spanId}`)
        if (record.type === 'span.end') span.open = false
      }
    }
  })
  for (const [epoch, state] of epochs)
    if (!state.stopped) issues.push({ message: `Incomplete source: ${epoch} has no source.stopped` })
  for (const [key, span] of spans)
    if (span.open) issues.push({ message: `Incomplete span: ${key} has no end` })
}

export async function loadRecording(selected: readonly LocalFile[], onProgress?: (progress: LoadProgress) => void | Promise<void>): Promise<Recording> {
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
  return loadDirectory({ name: prefix.split('/').filter(Boolean).at(-1) ?? recordFile.path, file: async path => files.get(path) }, onProgress)
}

export async function loadDirectory(directory: LocalDirectory, onProgress?: (progress: LoadProgress) => void | Promise<void>): Promise<Recording> {
  const progress = async (phase: LoadProgress['phase'], completed: number, total: number) => { await onProgress?.({ phase, completed, total }) }
  const recordFile = await directory.file('records.jsonl')
  if (!recordFile) throw new Error('Select the session folder containing records.jsonl and assets.')
  const issues: Issue[] = []
  const records: TraceRecord[] = []
  await progress('read', 0, recordFile.size)
  const text = new TextDecoder().decode(await recordFile.read())
  await progress('read', recordFile.size, recordFile.size)
  const lines = text.split(/\r?\n/)
  await progress('records', 0, lines.length)
  for (const [line, source] of lines.entries()) {
    if (line > 0 && line % 512 === 0) await progress('records', line, lines.length)
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
  await progress('records', lines.length, lines.length)
  if (!records.length) throw new Error('No native trace-kit records found. This viewer reads records.jsonl.')
  validateLifecycle(records, issues)
  const assets = new AssetStore(directory, issues)
  await progress('assets', 0, records.length)
  for (let start = 0; start < records.length; start += 32) {
    const end = Math.min(start + 32, records.length)
    await Promise.all(records.slice(start, end).map((record, offset) => assets.visitRecord(record, start + offset)))
    if (end % 512 === 0 || end === records.length) await progress('assets', end, records.length)
  }
  const full = createSessionViewReader(records[0].clock)
  await progress('timeline', 0, records.length)
  for (let start = 0; start < records.length; start += 4096) {
    full.reader.handleRecords(records.slice(start, start + 4096))
    await progress('timeline', Math.min(start + 4096, records.length), records.length)
  }
  const events = records.flatMap((record, index) => record.type === 'definition' || record.type === 'clock.snapshot' ? [] : [index])
  return { name: directory.name, records, issues, assets, full, events, index: new RecordingIndex(records, assets) }
}

export function payload(recording: Recording, record: TraceRecord): Record<string, unknown> {
  const data = recording.assets.data(record.attrs?.['airimayor.data'])
  return isObject(data) ? data : {}
}

export class Replay {
  readonly view: ViewReader
  cursor = -1

  constructor(readonly recording: Recording) {
    this.view = createSessionViewReader(recording.records[0].clock)
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
    for (const index of this.recording.index.through(this.recording.index.outputs.get(channelKey(channel)), this.cursor)) {
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
