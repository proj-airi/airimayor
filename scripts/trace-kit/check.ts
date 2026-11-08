import type { TraceRecord } from '@proj-airi/trace-kit'
import { readFile, readdir, realpath, stat } from 'node:fs/promises'
import { basename, isAbsolute, join, relative, resolve, win32 } from 'node:path'
import { fileURLToPath } from 'node:url'
import { TRACE_FORMAT, traceRecordSchema } from '@proj-airi/trace-kit'
import { createTraceViewReader } from '@proj-airi/trace-kit/views'
import { safeParse } from 'valibot'

const LEDGER_FIELDS = ['money', 'developmentPoints', 'buildings', 'networkEdges', 'transitLines']
const WITNESS_FIELDS: Record<string, string[]> = {
  place_building: ['buildings', 'money'],
  build_network: ['networkEdges', 'money'],
  replace_road_type: ['networkEdges', 'money'],
  set_road_features: ['money', 'networkEdges'],
  demolish: ['buildings', 'networkEdges'],
  set_facility_upgrade: ['buildings', 'money'],
  add_transit_line: ['transitLines'],
  remove_transit_line: ['transitLines'],
  expand_operational_area: ['money'],
  buy_tiles: ['money'],
  purchase_development_node: ['developmentPoints'],
}
const UNSEEN = new Set(['zone', 'set_budget', 'set_policy'])
type Verdict = 'confirmed' | 'diverged' | 'undetermined'

export class Report {
  failures = 0
  verdicts: Record<Verdict, number> = { confirmed: 0, diverged: 0, undetermined: 0 }

  constructor(readonly print: (message: string) => void = console.log) {}

  fail(where: string, message: string) {
    this.failures++
    this.print(`FAIL  ${where}: ${message}`)
  }

  verdict(where: string, verdict: Verdict, message: string) {
    this.verdicts[verdict]++
    if (verdict === 'diverged') this.failures++
    this.print(`${verdict.padEnd(12)}  ${where}: ${message}`)
  }
}

function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

class Assets {
  private checked = new Map<string, unknown>()
  private root?: string

  constructor(private directory: string, private report: Report) {}

  async read(ref: unknown, where: string): Promise<unknown> {
    if (!object(ref) || typeof ref.$asset !== 'string' || !ref.$asset.startsWith('assets/')
      || typeof ref.mediaType !== 'string' || !ref.mediaType
      || typeof ref.bytes !== 'number' || !Number.isSafeInteger(ref.bytes) || ref.bytes < 0) {
      this.report.fail(where, 'invalid asset reference')
      return
    }
    const path = ref.$asset
    if (isAbsolute(path) || win32.isAbsolute(path) || path.includes('\\')
      || path.split('/').some(part => !part || part === '.' || part === '..')) {
      this.report.fail(where, `unsafe asset path ${path}`)
      return
    }
    try {
      this.root ??= await realpath(this.directory)
      const actual = await realpath(join(this.root, path))
      const inside = relative(this.root, actual)
      if (isAbsolute(inside) || inside === '..' || inside.startsWith(`..${win32.sep}`) || inside.startsWith('../'))
        throw new Error('asset resolves outside the record')
      const info = await stat(actual)
      if (!info.isFile()) throw new Error('asset is not a file')
      if (info.size !== ref.bytes)
        throw new Error(`asset has ${info.size} bytes, reference says ${ref.bytes}`)
      const key = `${path}\0${ref.mediaType}`
      if (this.checked.has(key)) return this.checked.get(key)
      this.checked.set(key, undefined)
      if (ref.mediaType === 'application/json') {
        const data: unknown = JSON.parse(await readFile(actual, 'utf8'))
        this.checked.set(key, data)
        await this.visit(data, `${where} ${path}`)
        return data
      }
    }
    catch (error) {
      this.report.fail(where, `${path}: ${(error as Error).message}`)
    }
  }

  async visit(value: unknown, where: string): Promise<void> {
    if (Array.isArray(value)) {
      for (const item of value) await this.visit(item, where)
    }
    else if (object(value)) {
      if ('$asset' in value) await this.read(value, where)
      else for (const item of Object.values(value)) await this.visit(item, where)
    }
  }
}

function judgeWrite(where: string, tool: string, data: Record<string, unknown>, report: Report) {
  const success = Boolean(data.success)
  const claim = success ? 'succeeded' : 'failed'
  if (UNSEEN.has(tool)) {
    report.verdict(where, 'undetermined', `${claim}; the ledger does not see this write`)
    return
  }
  if (!data.evidence) {
    report.verdict(where, 'undetermined', `${claim}; the record holds no ledger evidence`)
    return
  }
  const { before, after } = object(data.evidence) ? data.evidence : {}
  if (!object(before) || !object(after)) {
    report.fail(where, 'ledger evidence needs before and after objects')
    return
  }
  const moved = LEDGER_FIELDS.filter(field => before[field] !== after[field])
  const change = moved.map(field => `${field} ${before[field]}->${after[field]}`).join(', ') || 'nothing moved'
  const clean = before.paused && after.paused && before.frame === after.frame
  if (!clean) {
    report.verdict(where, 'undetermined', `${claim}; simulation ran (frames ${before.frame}->${after.frame}); ${change}`)
  }
  else if (success) {
    if (WITNESS_FIELDS[tool].some(field => moved.includes(field)))
      report.verdict(where, 'confirmed', `succeeded; ${change}`)
    else
      report.verdict(where, 'undetermined', `reported success; ledger cannot confirm the requested change; ${change}`)
  }
  else if (moved.length) {
    report.verdict(where, 'diverged', `reported failure but the city changed; ${change}`)
  }
  else {
    report.verdict(where, 'confirmed', 'failed; nothing moved')
  }
}

async function checkCityResult(record: Extract<TraceRecord, { type: 'instant' }>, assets: Assets, report: Report, where: string) {
  const data = await assets.read(record.attrs?.['airimayor.data'], where)
  if (!object(data) || typeof data.tool !== 'string' || typeof data.success !== 'boolean') {
    report.fail(where, 'city.tool.result needs a JSON asset with tool and success')
    return
  }
  const label = `${where} ${data.tool} ${record.ids?.toolCall ?? record.spanId ?? ''}`
  const anchors = object(data.timeline) ? data.timeline : {}
  const frames = ['submitted', 'applied', 'completed']
    .filter(name => anchors[name] !== undefined).map(name => object(anchors[name]) ? anchors[name].frame : undefined)
  if (!frames.every((frame): frame is number => typeof frame === 'number' && Number.isSafeInteger(frame))
    || frames.some((frame, index) => index > 0 && frame < frames[index - 1]))
    report.fail(label, `anchors out of order or invalid: ${frames.join(', ')}`)
  if (data.files !== undefined && (!Array.isArray(data.files) || data.files.some(file =>
    !object(file) || typeof file.name !== 'string' || !object(file.asset) || !('$asset' in file.asset))))
    report.fail(label, 'files must contain names and native asset references')
  if (data.tool in WITNESS_FIELDS || UNSEEN.has(data.tool))
    judgeWrite(label, data.tool, data, report)
}

export async function checkRecord(directory: string, report: Report, label = basename(directory)) {
  report.print(`\n== ${label}`)
  let text: string
  try {
    text = await readFile(join(directory, 'records.jsonl'), 'utf8')
  }
  catch (error) {
    report.fail(label, `cannot read records.jsonl: ${(error as Error).message}`)
    return
  }
  const assets = new Assets(directory, report)
  const records: TraceRecord[] = []
  const epochs = new Map<string, { seq: number, stopped: boolean }>()
  const spans = new Map<string, { open: boolean, track: string }>()
  for (const [index, line] of text.split(/\r?\n/).entries()) {
    if (!line.trim()) continue
    const where = `${label}/records.jsonl:${index + 1}`
    let input: unknown
    try { input = JSON.parse(line) }
    catch { report.fail(where, 'invalid JSON'); continue }
    const parsed = safeParse(traceRecordSchema, input)
    if (!parsed.success) {
      report.fail(where, `invalid native trace record: ${parsed.issues.map(issue => issue.message).join('; ')}`)
      continue
    }
    const record = parsed.output
    records.push(record)
    const epoch = `${record.source}/${record.epoch}`
    const current = epochs.get(epoch)
    if (!Number.isSafeInteger(record.seq) || record.seq !== (current ? current.seq + 1 : 0))
      report.fail(where, `seq ${record.seq} does not follow ${current?.seq ?? 'start'}`)
    if (current?.stopped) report.fail(where, 'record follows source.stopped')
    if (record.type === 'source.started') {
      if (current) report.fail(where, 'source epoch starts more than once')
      if (record.format !== TRACE_FORMAT)
        report.fail(where, `format ${record.format} is not supported format ${TRACE_FORMAT}`)
    }
    else if (!current) {
      report.fail(where, 'source epoch has no source.started')
    }
    epochs.set(epoch, { seq: record.seq, stopped: current?.stopped || record.type === 'source.stopped' })
    if (record.type === 'source.lost') report.fail(where, `source ${record.of.source}/${record.of.epoch} was lost`)
    if ('spanId' in record && record.type.startsWith('span.')) {
      const key = `${epoch}/${record.spanId}`
      const span = spans.get(key)
      if (record.type === 'span.begin' || record.type === 'span.complete') {
        if (span) report.fail(where, `span ${record.spanId} starts more than once`)
        spans.set(key, { open: record.type === 'span.begin', track: record.track })
      }
      else if (!span?.open) {
        report.fail(where, `span ${record.spanId} ${record.type} has no open begin`)
      }
      else {
        if (span.track !== record.track) report.fail(where, `span ${record.spanId} changes track`)
        if (record.type === 'span.end') span.open = false
      }
    }
    await assets.visit(record, where)
    if (record.type === 'instant' && record.name === 'city.tool.result')
      await checkCityResult(record, assets, report, where)
    if (record.type === 'instant' && record.name === 'recording.gap') {
      const data = await assets.read(record.attrs?.['airimayor.data'], where)
      if (object(data)) report.print(`note  ${where}: known gap ${data.message ?? ''}`)
    }
    if (record.type === 'instant' && record.name === 'session.fact') {
      const data = await assets.read(record.attrs?.['airimayor.data'], where)
      if (object(data) && data.key === 'session' && object(data.value)) {
        const facts = data.value
        const details = ['head', 'city', 'provider', 'model', 'protocol']
          .filter(key => facts[key] !== null && facts[key] !== undefined)
          .map(key => `${key} ${facts[key]}`).join('; ')
        report.print(`session ${record.ids?.conversation ?? record.epoch}; ${details}`)
      }
    }
  }
  if (!records.length) report.fail(label, 'no native trace records')
  for (const [epoch, state] of epochs)
    if (!state.stopped) report.fail(label, `${epoch} has no source.stopped: the record was cut short`)
  for (const [key, span] of spans)
    if (span.open) report.fail(label, `span ${key} started and never ended`)

  const view = createTraceViewReader(records[0]?.clock ?? 'mono:record', { seriesRetention: false })
  view.reader.handleRecords(records)
  const timeline = view.timelineView()
  const modelSpans = [...timeline.spans.values()].filter(span => span.track.endsWith('/llm'))
  const toolSpans = [...timeline.spans.values()].filter(span => span.track.endsWith('/tools'))
  const usage = (key: string) => {
    const values = modelSpans.map(span => span.attrs[key]).filter(value => typeof value === 'number')
    return values.length ? values.reduce((sum, value) => sum + value, 0) : 'unavailable'
  }
  report.print(`trace: ${view.reader.indexes.byKey.size} records, ${timeline.spans.size} spans, ${modelSpans.length} model calls, ${toolSpans.length} tools; tokens: input ${usage('gen_ai.usage.input_tokens')}, output ${usage('gen_ai.usage.output_tokens')}`)

}

async function newestRecord() {
  const userData = process.env.CSII_USERDATAPATH ?? join(process.env.USERPROFILE ?? process.env.HOME ?? '', 'AppData', 'LocalLow', 'Colossal Order', 'Cities Skylines II')
  const root = join(userData, 'ModsData', 'airimayor', 'sessions')
  const entries = await readdir(root, { withFileTypes: true }).catch((error) => {
    if (error.code === 'ENOENT') return []
    throw error
  })
  for (const entry of entries.filter(entry => entry.isDirectory()).sort((a, b) => b.name.localeCompare(a.name))) {
    const directory = join(root, entry.name)
    if (await stat(join(directory, 'records.jsonl')).catch(() => null)) return directory
  }
}

async function main() {
  const directory = process.argv[2] ? resolve(process.argv[2]) : await newestRecord()
  if (!directory) {
    console.log('No session record found. Pass a record directory or set CSII_USERDATAPATH.')
    return 2
  }
  const report = new Report()
  await checkRecord(directory, report)
  report.print(`\nwrites: ${Object.entries(report.verdicts).map(([verdict, count]) => `${count} ${verdict}`).join(', ')}; failures: ${report.failures}`)
  return report.failures ? 1 : 0
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().then(code => { process.exitCode = code }, (error) => {
    console.error(`FAIL  ${(error as Error).message}`)
    process.exitCode = 1
  })
}
