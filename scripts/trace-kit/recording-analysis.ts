import type { Recording, Span } from './viewer-model'
import { spanKeyOf } from '@proj-airi/trace-kit'
import { isAsset, isObject, payload } from './viewer-model'
import { judgeWrite, type Verdict } from './write-evidence'

export type Outcome = 'ok' | 'error' | 'cancelled' | 'incomplete'
export interface AnalysisFilter {
  agent?: string
  run?: string
  turn?: string
  status?: string
  tool?: string
}
export interface SearchQuery extends AnalysisFilter {
  text?: string
  offset?: number
  limit?: number
}
export interface SearchHit {
  index: number
  time: number
  name: string
  track: string
  status?: string
  snippet: string
}
export interface SearchResult {
  total: number
  hits: SearchHit[]
}
export interface Usage {
  input?: number
  output?: number
  cache?: number
  reasoning?: number
}
export interface ModelCall extends Usage {
  key: string
  index: number
  time: number
  agent: string
  run?: string
  turn?: string
  status: Outcome
  duration?: number
  ttft?: number
  ttfat?: number
  cacheRate?: number
  inputGrowth?: number
  model?: string
}
export interface Distribution {
  count: number
  p50?: number
  p95?: number
}
export interface ToolSummary {
  name: string
  calls: number
  ok: number
  error: number
  cancelled: number
  incomplete: number
  duration: Distribution
  queued: Distribution
  confirmed: number
  diverged: number
  undetermined: number
  invalid: number
}
export interface Compaction {
  index: number
  time: number
  agent: string
  run?: string
  turn?: string
  status: string
  changed: boolean
  before?: number
  after?: number
  budget?: number
  trigger?: number
  target?: number
}
export interface AnalysisResult {
  calls: number
  outcomes: Record<Outcome, number>
  usage: Record<keyof Usage, { total?: number; calls: number }>
  cacheRate?: number
  cacheCalls: number
  latency: { duration: Distribution; ttft: Distribution; ttfat: Distribution }
  agents: { name: string; calls: number; input?: number; output?: number }[]
  models: ModelCall[]
  modelTotal: number
  tools: ToolSummary[]
  compactions: Compaction[]
  compactionTotal: number
  options: { agents: string[]; runs: string[]; turns: string[]; tools: string[] }
  gaps: string[]
}

interface CityCall {
  index: number
  key?: string
  name: string
  ids: Record<string, string | number>
  status: Outcome
  duration?: number
  queued?: number
  verdict?: Verdict
  invalid: boolean
}

const usageKeys = {
  input: 'gen_ai.usage.input_tokens',
  output: 'gen_ai.usage.output_tokens',
  cache: 'gen_ai.usage.cache_read.input_tokens',
  reasoning: 'gen_ai.usage.reasoning.output_tokens',
} as const
const numeric = (value: unknown): number | undefined =>
  typeof value === 'number' && Number.isFinite(value) ? value : undefined
const outcome = (span?: Span): Outcome =>
  span?.end === undefined
    ? 'incomplete'
    : span.status === 'ok'
      ? 'ok'
      : span.status === 'cancelled'
        ? 'cancelled'
        : 'error'
const distribution = (values: (number | undefined)[]): Distribution => {
  const sorted = values.filter((value): value is number => value !== undefined).sort((a, b) => a - b)
  const percentile = (p: number) => (sorted.length ? sorted[Math.max(0, Math.ceil(sorted.length * p) - 1)] : undefined)
  return { count: sorted.length, p50: percentile(0.5), p95: percentile(0.95) }
}
function matches(
  ids: Record<string, string | number>,
  status: string | undefined,
  tool: string | undefined,
  filter: AnalysisFilter,
) {
  return (
    (!filter.agent || ids.agent === filter.agent) &&
    (!filter.run || ids.run === filter.run || ids.originRun === filter.run) &&
    (!filter.turn || ids.turn === filter.turn || ids.originTurn === filter.turn) &&
    (!filter.status || status === filter.status) &&
    (!filter.tool || tool === filter.tool)
  )
}

function idsOf(record: { ids?: Record<string, string | number>; track?: string }): Record<string, string | number> {
  return { ...(record.track?.startsWith('agents/') ? { agent: record.track.split('/')[1] } : {}), ...record.ids }
}

export class RecordingAnalysis {
  private models: ModelCall[] = []
  private city: CityCall[] = []
  private compactions: Compaction[] = []
  private gaps: string[] = []
  private options: AnalysisResult['options']
  private modelIds = new Map<string, Record<string, string | number>>()
  private recordSpans = new Map<number, Span>()
  private compactionIds = new Map<number, Record<string, string | number>>()
  private searchVersion = 0
  private cityBySpan = new Map<string, CityCall>()

  constructor(private recording: Recording) {
    const spans = recording.full.timelineView().spans
    const origin = recording.full.reader.timeOf(recording.records[0]).time
    const time = (index: number) => recording.full.reader.timeOf(recording.records[index]).time - origin
    const represented = new Set<string>()
    recording.records.forEach((record, index) => {
      const key =
        'spanId' in record && record.spanId ? spanKeyOf(record.source, record.epoch, record.spanId) : undefined
      const span = key ? spans.get(key) : undefined
      if (span) this.recordSpans.set(index, span)
      if (record.type !== 'instant') return
      const data = payload(recording, record)
      if (record.name === 'recording.gap' && typeof data.message === 'string') this.gaps.push(data.message)
      if (record.name === 'city.tool.result' && typeof data.tool === 'string') {
        if (key) represented.add(key)
        const judgment = judgeWrite(data.tool, data)
        this.city.push({
          index,
          key,
          name: data.tool,
          ids: {
            ...idsOf(record),
            ...span?.ids,
            ...record.ids,
            ...(typeof data.caller === 'string' ? { agent: data.caller } : {}),
          },
          status: data.success
            ? 'ok'
            : data.status === 'cancelled' || (span && outcome(span) === 'cancelled')
              ? 'cancelled'
              : 'error',
          duration: numeric(data.elapsedMs),
          queued: numeric(data.queuedMs),
          verdict: judgment.verdict,
          invalid: Boolean(judgment.invalid),
        })
      }
      if (record.name === 'compaction.finished') {
        const before = recording.assets.data(data.before),
          after = recording.assets.data(data.after)
        const ids = { ...span?.ids, ...record.ids }
        this.compactionIds.set(index, ids)
        this.compactions.push({
          index,
          time: time(index),
          agent: String(ids.agent ?? ''),
          run: String(ids.run ?? ''),
          turn: String(ids.turn ?? ids.originTurn ?? ''),
          status: String(data.status ?? 'unknown'),
          changed: data.changed === true,
          before: isObject(before) ? numeric(before.tokenCount) : undefined,
          after: isObject(after) ? numeric(after.tokenCount) : undefined,
          budget: numeric(data.inputBudgetTokens),
          trigger: numeric(data.triggerTokens),
          target: numeric(data.targetTokens),
        })
      }
    })
    const previous = new Map<string, number>()
    for (const span of spans.values()) {
      const ids = idsOf(span)
      const indices = recording.index.spans.get(span.key) ?? []
      if (span.track.endsWith('/tools') && !represented.has(span.key))
        this.city.push({
          index: indices[0],
          key: span.key,
          name: String(span.attrs['gen_ai.tool.name'] ?? span.name),
          ids,
          status: outcome(span),
          duration: span.end === undefined ? undefined : span.end - span.start,
          invalid: false,
        })
      if (!span.track.endsWith('/llm')) continue
      this.modelIds.set(span.key, ids)
      const input = numeric(span.attrs[usageKeys.input]),
        cache = numeric(span.attrs[usageKeys.cache])
      const identity = String(ids.agentSession ?? ids.agent ?? span.track)
      const prior = previous.get(identity)
      const started = span.start - origin
      this.models.push({
        key: span.key,
        index: indices[0],
        time: started,
        agent: String(ids.agent ?? span.track.split('/')[1]),
        run: String(ids.run ?? ''),
        turn: String(ids.turn ?? ids.originTurn ?? ''),
        status: outcome(span),
        model: typeof span.attrs['gen_ai.request.model'] === 'string' ? span.attrs['gen_ai.request.model'] : undefined,
        input,
        cache,
        output: numeric(span.attrs[usageKeys.output]),
        reasoning: numeric(span.attrs[usageKeys.reasoning]),
        cacheRate: input && cache !== undefined ? cache / input : undefined,
        inputGrowth: input !== undefined && prior !== undefined ? input - prior : undefined,
        duration: numeric(span.attrs['airimayor.duration_ms']),
        ttft: numeric(span.attrs['llm.ttft_ms']),
        ttfat: numeric(span.attrs['llm.ttfat_ms']),
      })
      if (input !== undefined) previous.set(identity, input)
    }
    const ids = recording.records.map((record) => record.ids ?? {})
    const values = (key: string) =>
      [...new Set(ids.flatMap((id) => (id[key] === undefined ? [] : [String(id[key])])))].sort()
    this.options = {
      agents: [
        ...new Set(
          this.models
            .map((call) => call.agent)
            .concat(
              values('agent'),
              this.city.flatMap((call) => (typeof call.ids.agent === 'string' ? [call.ids.agent] : [])),
            ),
        ),
      ].sort(),
      runs: values('run'),
      turns: values('turn'),
      tools: [...new Set(this.city.map((call) => call.name))].sort(),
    }
    this.city.forEach((call) => {
      if (call.key) this.cityBySpan.set(call.key, call)
    })
  }

  analyze(filter: AnalysisFilter = {}, offset = 0, limit = 100): AnalysisResult {
    const models = this.models.filter((call) => matches(this.modelIds.get(call.key)!, call.status, undefined, filter))
    const selectedTools = this.city.filter((call) => matches(call.ids, call.status, call.name, filter))
    const tools = [...new Set(selectedTools.map((call) => call.name))]
      .map((name) => {
        const calls = selectedTools.filter((call) => call.name === name)
        const count = (status: Outcome) => calls.filter((call) => call.status === status).length
        return {
          name,
          calls: calls.length,
          ok: count('ok'),
          error: count('error'),
          cancelled: count('cancelled'),
          incomplete: count('incomplete'),
          duration: distribution(calls.filter((call) => call.status === 'ok').map((call) => call.duration)),
          queued: distribution(calls.map((call) => call.queued)),
          confirmed: calls.filter((call) => call.verdict === 'confirmed').length,
          diverged: calls.filter((call) => call.verdict === 'diverged').length,
          undetermined: calls.filter((call) => call.verdict === 'undetermined').length,
          invalid: calls.filter((call) => call.invalid).length,
        }
      })
      .sort((a, b) => b.calls - a.calls || a.name.localeCompare(b.name))
    const sum = (calls: ModelCall[], key: keyof Usage) => {
      const reported = calls.flatMap((call) => (call[key] === undefined ? [] : [call[key]!]))
      return { total: reported.length ? reported.reduce((a, b) => a + b, 0) : undefined, calls: reported.length }
    }
    const usage = {
      input: sum(models, 'input'),
      output: sum(models, 'output'),
      cache: sum(models, 'cache'),
      reasoning: sum(models, 'reasoning'),
    }
    const paired = models.filter((call) => call.input !== undefined && call.cache !== undefined)
    const denominator = paired.reduce((sum, call) => sum + call.input!, 0)
    const successful = models.filter((call) => call.status === 'ok')
    const compactions = this.compactions.filter((call) =>
      matches(this.compactionIds.get(call.index)!, call.status, undefined, filter),
    )
    return {
      calls: models.length,
      outcomes: Object.fromEntries(
        ['ok', 'error', 'cancelled', 'incomplete'].map((status) => [
          status,
          models.filter((call) => call.status === status).length,
        ]),
      ) as Record<Outcome, number>,
      usage,
      cacheRate: denominator ? paired.reduce((sum, call) => sum + call.cache!, 0) / denominator : undefined,
      cacheCalls: paired.length,
      latency: {
        duration: distribution(successful.map((call) => call.duration)),
        ttft: distribution(successful.map((call) => call.ttft)),
        ttfat: distribution(successful.map((call) => call.ttfat)),
      },
      agents: [...new Set(models.map((call) => call.agent))].map((name) => {
        const calls = models.filter((call) => call.agent === name)
        return { name, calls: calls.length, input: sum(calls, 'input').total, output: sum(calls, 'output').total }
      }),
      models: models.slice(offset, offset + limit),
      modelTotal: models.length,
      tools,
      compactions: compactions.slice(-100),
      compactionTotal: compactions.length,
      options: this.options,
      gaps: this.gaps,
    }
  }

  async search(query: SearchQuery = {}): Promise<SearchResult> {
    const version = ++this.searchVersion
    const needle = query.text?.trim().toLowerCase() ?? ''
    const cached = new Map<string, Promise<string | undefined>>()
    const snippet = (text: string) => {
      const index = text.toLowerCase().indexOf(needle)
      return index < 0 ? undefined : text.slice(Math.max(0, index - 65), index + needle.length + 135)
    }
    const find = async (value: unknown, seen = new Set<string>()): Promise<string | undefined> => {
      if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean')
        return snippet(String(value))
      if (isAsset(value)) {
        const key = `${value.$asset}\0${value.mediaType}`
        if (seen.has(key)) return
        let found = cached.get(key)
        if (!found) {
          const visited = new Set([...seen, key])
          found = (async () => {
            if (value.mediaType === 'application/json') return find(this.recording.assets.data(value), visited)
            if (value.mediaType.startsWith('text/')) {
              const file = await this.recording.assets.file(value)
              if (file) return snippet(new TextDecoder().decode(await file.read()))
            }
          })()
          cached.set(key, found)
        }
        return found
      }
      for (const item of Array.isArray(value) ? value : isObject(value) ? Object.values(value) : []) {
        const found = await find(item, seen)
        if (found !== undefined) return found
      }
    }
    const offset = Math.max(0, query.offset ?? 0),
      limit = Math.min(200, Math.max(1, query.limit ?? 50))
    const hits: SearchHit[] = []
    let total = 0
    const origin = this.recording.full.reader.timeOf(this.recording.records[0]).time
    for (const [index, record] of this.recording.records.entries()) {
      if (index % 512 === 0) {
        await new Promise((resolve) => setTimeout(resolve, 0))
        if (version !== this.searchVersion) return { total: 0, hits: [] }
      }
      if (record.type === 'definition' || record.type === 'clock.snapshot') continue
      const span = this.recordSpans.get(index)
      const data = record.type === 'instant' ? payload(this.recording, record) : undefined
      const city = span ? this.cityBySpan.get(span.key) : undefined
      const status = city
        ? city.status
        : data && typeof data.status === 'string'
          ? data.status
          : span
            ? outcome(span)
            : record.type === 'log' && record.level === 'error'
              ? 'error'
              : undefined
      const tool =
        city?.name ??
        data?.tool ??
        (span?.track.endsWith('/tools') ? (span.attrs['gen_ai.tool.name'] ?? span.name) : undefined)
      if (
        !matches(
          { ...idsOf(record), ...span?.ids, ...record.ids, ...city?.ids },
          status,
          typeof tool === 'string' ? tool : undefined,
          query,
        )
      )
        continue
      const match = needle ? await find(record) : ''
      if (match === undefined) continue
      if (total++ < offset || hits.length >= limit) continue
      hits.push({
        index,
        time: this.recording.full.reader.timeOf(record).time - origin,
        name: 'name' in record ? String(record.name) : record.type === 'log' ? record.message : record.type,
        track: 'track' in record ? record.track : '',
        status,
        snippet: match,
      })
    }
    return { total, hits }
  }
}
