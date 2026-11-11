import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createTraceViewReader } from '@proj-airi/trace-kit/views'
import { profileDefinitions } from './profile'
import { ViewerSession } from './viewer-session'

function fixture() {
  const records: object[] = [],
    files: { path: string; file: Blob }[] = []
  const add = (record: object) => {
    const index = records.length
    records.push({ source: 'game', epoch: 'test', clock: 'mono:test', seq: index, ts: index * 10, ...record })
    return index
  }
  const asset = (data: unknown, mediaType = 'application/json') => {
    const file = new Blob([mediaType === 'application/json' ? JSON.stringify(data) : String(data)])
    const path = `assets/${files.length}`
    files.push({ path: `session/${path}`, file })
    return { $asset: path, mediaType, bytes: file.size }
  }
  add({ type: 'source.started', format: 1, runtime: 'dotnet', profiles: [] })
  const open = async () => {
    const session = new ViewerSession()
    const summary = await session.openFiles(
      [
        { path: 'session/records.jsonl', file: new Blob([records.map((record) => JSON.stringify(record)).join('\n')]) },
        ...files,
      ],
      () => {},
    )
    return { session, summary }
  }
  return { add, asset, open }
}

test('recorded mayor metrics reach the actual AIRI metric projection', async () => {
  const trace = fixture()
  profileDefinitions.forEach(trace.add)
  trace.add({ type: 'span.begin', spanId: 'model', track: 'agents/mayor/llm', name: 'chat' })
  trace.add({
    type: 'span.end',
    spanId: 'model',
    track: 'agents/mayor/llm',
    status: 'ok',
    attrs: {
      'llm.ttft_ms': 20,
      'llm.ttfat_ms': 30,
      'airimayor.duration_ms': 40,
      'gen_ai.usage.input_tokens': 100,
      'gen_ai.usage.output_tokens': 10,
      'gen_ai.usage.cache_read.input_tokens': 80,
      'gen_ai.usage.reasoning.output_tokens': 5,
    },
  })
  trace.add({ type: 'source.stopped' })
  const { summary, session } = await trace.open()
  const view = createTraceViewReader(summary.clock, { seriesRetention: false })
  view.reader.handleRecords(summary.overview)
  const samples = view.metricSamples()
  assert.equal(samples['llm.ttft_ms'][0].value, 20)
  assert.equal(samples['llm.ttfat_ms'][0].value, 30)
  assert.equal(samples['model.duration_ms'][0].value, 40)
  assert.equal(samples['model.cache_tokens'][0].value, 80)
  assert.equal(samples['model.reasoning_tokens'][0].value, 5)
  assert.equal(session.analyze().models[0].duration, 40)
})

test('analysis preserves missing usage, weights only paired cache samples, separates outcomes and agent sessions', async () => {
  const trace = fixture()
  const model = (id: string, agent: string, status: string, attrs: object, agentSession = agent) => {
    const ids = {
      agent,
      agentSession,
      run: id,
      ...(agent === 'mayor' ? { turn: 'root' } : { originTurn: 'root', originRun: 'one' }),
    }
    trace.add({ type: 'span.begin', spanId: id, track: `agents/${agent}/llm`, name: 'chat', ids })
    trace.add({ type: 'span.end', spanId: id, track: `agents/${agent}/llm`, status, ids,
      attrs: { 'airimayor.duration_ms': 10, ...attrs } })
  }
  model('one', 'mayor', 'ok', {
    'gen_ai.usage.input_tokens': 100,
    'gen_ai.usage.cache_read.input_tokens': 50,
    'gen_ai.usage.output_tokens': 10,
  })
  model('two', 'mayor', 'error', { 'gen_ai.usage.input_tokens': 900, 'gen_ai.usage.cache_read.input_tokens': 900 })
  model('three', 'inspector', 'cancelled', { 'gen_ai.usage.input_tokens': 200, 'gen_ai.usage.output_tokens': 4 })
  model('new-session', 'mayor', 'ok', { 'gen_ai.usage.input_tokens': 10 }, 'new-mayor')
  trace.add({
    type: 'span.begin',
    spanId: 'open',
    track: 'agents/inspector/llm',
    name: 'chat',
    ids: { agent: 'inspector' },
  })
  trace.add({ type: 'source.stopped' })
  const { session } = await trace.open()
  const result = session.analyze()
  assert.deepEqual(result.outcomes, { ok: 2, error: 1, cancelled: 1, incomplete: 1 })
  assert.deepEqual(result.usage.input, { total: 1210, calls: 4 })
  assert.deepEqual(result.usage.reasoning, { total: undefined, calls: 0 })
  assert.equal(result.cacheRate, 0.95)
  assert.equal(result.cacheCalls, 2)
  assert.equal(result.latency.duration.count, 2)
  assert.equal(result.models[1].inputGrowth, 800)
  assert.equal(result.models[3].inputGrowth, undefined)
  assert.equal(session.analyze({ run: 'one', turn: 'root' }).calls, 2)
  assert.equal(session.analyze({ status: 'cancelled' }).calls, 1)
  assert.equal(session.analyze({}, 2).models[0].run, 'three')
})

test('latency uses captured metrics and leaves missing values unavailable instead of inferring them from output', async () => {
  const trace = fixture()
  trace.add({ type: 'span.begin', spanId: 'call', track: 'agents/mayor/llm', name: 'chat', ts: 100 })
  const delta = (ts: number, data: object) =>
    trace.add({
      type: 'instant',
      name: 'model.output.delta',
      track: 'agents/mayor/llm',
      spanId: 'call',
      ts,
      attrs: { 'airimayor.data': JSON.stringify(data) },
    })
  delta(110, { update: { contents: [] } })
  delta(120, { update: { contents: [{ $type: 'usage', details: {} }] } })
  delta(130, {
    reasoning: 'River inspection',
    update: { contents: [{ $type: 'textReasoning', text: 'River inspection' }] },
  })
  delta(150, { update: { contents: [{ $type: 'functionCall', name: 'map_text', callId: 'tool' }] } })
  trace.add({ type: 'span.end', spanId: 'call', track: 'agents/mayor/llm', status: 'ok', ts: 200,
    attrs: { 'llm.ttft_ms': 31, 'llm.ttfat_ms': 55 } })
  trace.add({ type: 'span.begin', spanId: 'without-metrics', track: 'agents/mayor/llm', name: 'chat', ts: 201 })
  trace.add({ type: 'instant', name: 'model.output.delta', spanId: 'without-metrics', track: 'agents/mayor/llm', ts: 202,
    attrs: { 'airimayor.data': JSON.stringify({ text: 'An observed answer without captured latency' }) } })
  trace.add({ type: 'span.end', spanId: 'without-metrics', track: 'agents/mayor/llm', status: 'ok', ts: 203 })
  trace.add({ type: 'source.stopped', ts: 210 })
  const { session } = await trace.open()
  const calls = session.analyze().models
  assert.equal(calls[0].ttft, 31)
  assert.equal(calls[0].ttfat, 55)
  assert.equal(calls[1].ttft, undefined)
  assert.equal(calls[1].ttfat, undefined)
  assert.equal(calls[1].duration, undefined)
})

test('tool summaries count each city result once, use reported failure, and share ledger judgments with the checker', async () => {
  const trace = fixture()
  const before = { paused: true, frame: 1, money: 100, buildings: 0 }
  for (const [id, success, status, after] of [
    ['success', true, 'ok', { ...before, buildings: 1 }],
    ['diverged', false, 'ok', { ...before, buildings: 1 }],
    ['cancelled', false, 'cancelled', before],
  ] as const) {
    trace.add({
      type: 'span.begin',
      spanId: id,
      track: 'agents/mayor/tools',
      name: 'execute_tool',
      ids: { agent: 'mayor', run: 'root' },
    })
    trace.add({
      type: 'instant',
      name: 'city.tool.result',
      spanId: id,
      track: 'agents/mayor/tools',
      attrs: {
        'airimayor.data': trace.asset({
          tool: 'place_building',
          success,
          elapsedMs: 20,
          queuedMs: 5,
          evidence: { before, after },
        }),
      },
    })
    trace.add({
      type: 'span.end',
      spanId: id,
      track: 'agents/mayor/tools',
      status,
      attrs: { 'gen_ai.tool.name': 'place_building' },
    })
  }
  trace.add({
    type: 'span.begin',
    spanId: 'unfinished',
    track: 'agents/mayor/tools',
    name: 'execute_tool',
    attrs: { 'gen_ai.tool.name': 'place_building' },
  })
  trace.add({ type: 'source.stopped' })
  const { session } = await trace.open()
  const tools = session.analyze().tools
  assert.equal(tools.length, 1)
  assert.deepEqual(tools[0], {
    name: 'place_building',
    calls: 4,
    ok: 1,
    error: 1,
    cancelled: 1,
    incomplete: 1,
    duration: { count: 1, p50: 20, p95: undefined },
    queued: { count: 3, p50: 5, p95: undefined },
    confirmed: 2,
    diverged: 1,
    undetermined: 0,
    invalid: 0,
  })
  assert.equal((await session.search({ tool: 'place_building', status: 'error' })).total, 3)
})

test('compaction exposes before/after history estimates and budget without resolving message snapshots', async () => {
  const trace = fixture()
  trace.add({
    type: 'instant',
    name: 'compaction.finished',
    track: 'city/events',
    ids: { agent: 'mayor', run: 'root', turn: 'turn' },
    attrs: {
      'airimayor.data': trace.asset({
        changed: true,
        status: 'ok',
        before: trace.asset({ tokenCount: 1000 }),
        after: trace.asset({ tokenCount: 200 }),
        inputBudgetTokens: 1500,
        triggerTokens: 750,
        targetTokens: 300,
      }),
    },
  })
  trace.add({ type: 'source.stopped' })
  const { session } = await trace.open()
  const call = session.analyze().compactions[0]
  assert.equal(call.before, 1000)
  assert.equal(call.after, 200)
  assert.equal(call.budget, 1500)
  assert.equal(session.analyze({ run: 'root' }).compactionTotal, 1)
})

test('full-text search follows shared nested JSON and text assets, intersects filters, pages results and navigates exact records', async () => {
  const trace = fixture()
  const text = trace.asset('Bridge feasibility at the river', 'text/plain')
  const nested = trace.asset({ parameters: { note: text } })
  const ids = { agent: 'inspector', run: 'child', originRun: 'root', originTurn: 'turn' }
  trace.add({ type: 'span.begin', spanId: 'child', track: 'agents/inspector/llm', name: 'chat', ids })
  const checkpoint = trace.add({
    type: 'span.update',
    spanId: 'child',
    track: 'agents/inspector/llm',
    attrs: { 'gen_ai.input.messages': nested },
    ids,
  })
  trace.add({
    type: 'span.end',
    spanId: 'child',
    track: 'agents/inspector/llm',
    status: 'error',
    attrs: { 'gen_ai.output.messages': nested },
    ids,
  })
  trace.add({ type: 'source.stopped' })
  const { session, summary } = await trace.open()
  const query = { text: 'BRIDGE feasibility', run: 'root', turn: 'turn', agent: 'inspector', status: 'error' }
  const first = await session.search({ ...query, limit: 1 })
  assert.equal(first.total, 2)
  assert.equal(first.hits[0].index, checkpoint)
  assert.match(first.hits[0].snippet, /Bridge feasibility/)
  const second = await session.search({ ...query, limit: 1, offset: 1 })
  assert.equal(second.hits.length, 1)
  assert.notEqual(first.hits[0].index, second.hits[0].index)
  assert.equal((await session.search({ ...query, status: 'ok' })).total, 0)
  const position = summary.events.findIndex((event) => event.index === checkpoint)
  const frame = session.frame(position)
  assert.equal(frame.index, checkpoint)
  assert.equal(frame.state, 'In progress')
})

test('a superseded long search yields to the latest query and leaves analysis reachable', async () => {
  const trace = fixture()
  const data = trace.asset({ text: 'river crossing' })
  for (let index = 0; index < 4096; index++)
    trace.add({ type: 'instant', name: 'note', track: 'city/events', attrs: { 'airimayor.data': data } })
  trace.add({ type: 'source.stopped' })
  const { session } = await trace.open()
  const stale = session.search({ text: 'river' })
  const current = session.search({ text: 'crossing', offset: 50, limit: 20 })
  assert.equal(session.analyze().calls, 0)
  assert.deepEqual(await stale, { total: 0, hits: [] })
  const result = await current
  assert.equal(result.total, 4096)
  assert.equal(result.hits.length, 20)
})
