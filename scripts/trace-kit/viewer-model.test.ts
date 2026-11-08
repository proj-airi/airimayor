import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { test } from 'node:test'
import { spansOfLink } from '@proj-airi/trace-kit/views'
import { loadRecording, Replay, type LocalFile } from './viewer-model'

function fixture() {
  const records: object[] = []
  const assets: LocalFile[] = []
  const file = (path: string, text: string): LocalFile => {
    const bytes = new TextEncoder().encode(text)
    return { path, size: bytes.byteLength, read: async () => bytes }
  }
  const add = (value: object) => {
    const index = records.length
    records.push({ source: 'cs2:game', epoch: 'record', clock: 'mono:record', seq: index, ts: index * 100, ...value })
    return index
  }
  const asset = (value: unknown) => {
    const json = JSON.stringify(value)
    const hash = createHash('sha256').update(json).digest('hex')
    const result = { $asset: `assets/${hash}`, mediaType: 'application/json', bytes: Buffer.byteLength(json) }
    if (!assets.some(item => item.path === `session/${result.$asset}`)) assets.push(file(`session/${result.$asset}`, json))
    return result
  }
  const delta = (spanId: string, index: number, text: string, reasoning = '') => add({ type: 'instant', track: 'agents/mayor/llm', spanId, name: 'model.output.delta', attrs: {
    'airimayor.data': asset({ index, text, reasoning, update: { contents: [{ type: 'text', text }] } }),
  } })
  const files = (suffix = '') => [file('session/records.jsonl', records.map(record => JSON.stringify(record)).join('\n') + suffix), ...assets]
  add({ type: 'source.started', format: 1, runtime: 'dotnet', profiles: [{ key: 'agent', version: '1' }] })
  return { add, asset, delta, files, file, records }
}

test('replay follows every actual delta, supports backwards seek, and never borrows final text', async () => {
  const trace = fixture()
  trace.add({ type: 'span.begin', spanId: 'model', name: 'chat', track: 'agents/mayor/llm' })
  trace.add({ type: 'span.update', spanId: 'model', track: 'agents/mayor/llm', attrs: { 'gen_ai.input.messages': trace.asset([{ role: 'user', content: 'Build a city' }]) } })
  const first = trace.delta('model', 0, 'Hello ', 'Consider ')
  const second = trace.delta('model', 1, 'city', 'roads.')
  const end = trace.add({ type: 'span.end', spanId: 'model', track: 'agents/mayor/llm', status: 'ok', attrs: { 'gen_ai.output.messages': trace.asset([{ content: 'Different final text' }]) } })
  trace.add({ type: 'source.stopped' })
  const recording = await loadRecording(trace.files())
  assert.equal(recording.issues.length, 0)
  const replay = new Replay(recording)
  const channel = { kind: 'model', source: 'cs2:game', epoch: 'record', spanId: 'model' } as const
  replay.seek(first - 1)
  assert.equal(replay.output(channel).chunks, 0)
  replay.seek(first)
  assert.equal(replay.output(channel).text, 'Hello ')
  replay.seek(second)
  assert.equal(replay.output(channel).text, 'Hello city')
  assert.equal(replay.output(channel).reasoning, 'Consider roads.')
  assert.equal(replay.output(channel).chunks, 2)
  replay.seek(end)
  assert.equal(replay.output(channel).text, 'Hello city')
  replay.seek(first)
  assert.equal(replay.output(channel).text, 'Hello ')
  assert.equal(replay.view.timelineView().spans.get('cs2:game/record/model')!.attrs['gen_ai.output.messages'], undefined)
})

test('non-streaming final content becomes available only at its end, without invented updates', async () => {
  const trace = fixture()
  trace.add({ type: 'span.begin', spanId: 'child', name: 'chat', track: 'agents/inspector/llm' })
  const end = trace.add({ type: 'span.end', spanId: 'child', track: 'agents/inspector/llm', status: 'ok', attrs: { 'gen_ai.output.messages': trace.asset([{ content: 'Child report' }]) } })
  trace.add({ type: 'source.stopped' })
  const recording = await loadRecording(trace.files())
  const replay = new Replay(recording)
  replay.seek(end - 1)
  assert.equal(replay.view.timelineView().spans.get('cs2:game/record/child')!.attrs['gen_ai.output.messages'], undefined)
  replay.seek(end)
  assert.deepEqual(recording.assets.data(replay.view.timelineView().spans.get('cs2:game/record/child')!.attrs['gen_ai.output.messages']), [{ content: 'Child report' }])
  assert.equal(replay.output({ kind: 'model', source: 'cs2:game', epoch: 'record', spanId: 'child' }).chunks, 0)
})

test('long streamed sessions remain indexed and replayable without quadratic projection work', async () => {
  const trace = fixture()
  const deltas = Array.from({ length: 200 }, (_, index) => trace.asset({ index, text: 'x' }))
  for (let call = 0; call < 200; call++) {
    const spanId = `model-${call}`
    trace.add({ type: 'span.begin', spanId, name: 'chat', track: 'agents/mayor/llm' })
    for (let index = 0; index < 200; index++)
      trace.add({ type: 'instant', spanId, track: 'agents/mayor/llm', name: 'model.output.delta', attrs: { 'airimayor.data': deltas[index] } })
    trace.add({ type: 'span.end', spanId, track: 'agents/mayor/llm', status: 'ok' })
  }
  trace.add({ type: 'source.stopped' })
  const files = trace.files()
  const started = performance.now()
  const recording = await loadRecording(files)
  const replay = new Replay(recording)
  replay.seek(recording.records.length - 1)
  assert.equal(recording.full.reader.indexes.byKey.size, trace.records.length)
  assert.equal(replay.view.reader.indexes.byKey.size, trace.records.length)
  assert.equal(replay.view.timelineView().spans.size, 200)
  assert.equal(replay.output({ kind: 'model', source: 'cs2:game', epoch: 'record', spanId: 'model-199' }).chunks, 200)
  assert.ok(performance.now() - started < 2000, '40,000 updates must load and seek within two seconds')
})

test('reconstructs ACP output separately for root and owned conversations', async () => {
  const trace = fixture()
  for (const [agentSession, index, text, reasoning] of [['root', 0, 'Mayor ', ''], ['child', 0, 'Report', 'Inspect'], ['root', 1, 'reply', '']] as const)
    trace.add({ type: 'instant', name: 'acp.output.delta', track: 'city/events', attrs: { 'airimayor.data': trace.asset({ agentSession, index, text, reasoning }) } })
  trace.add({ type: 'source.stopped' })
  const replay = new Replay(await loadRecording(trace.files()))
  replay.seek(2)
  assert.equal(replay.output({ kind: 'acp', agentSession: 'root' }).text, 'Mayor ')
  assert.equal(replay.output({ kind: 'acp', agentSession: 'child' }).reasoning, 'Inspect')
  replay.seek(4)
  assert.equal(replay.output({ kind: 'acp', agentSession: 'root' }).text, 'Mayor reply')
})

test('loads an aborted prefix and reports incomplete spans and malformed trailing JSON', async () => {
  const trace = fixture()
  trace.add({ type: 'span.begin', spanId: 'aborted', name: 'chat', track: 'agents/mayor/llm' })
  const at = trace.delta('aborted', 0, 'Durable prefix')
  const recording = await loadRecording(trace.files('\n{"type":'))
  assert.ok(recording.issues.some(issue => issue.message.includes('truncated JSON')))
  assert.ok(recording.issues.some(issue => issue.message.includes('Incomplete source')))
  assert.ok(recording.issues.some(issue => issue.message.includes('Incomplete span')))
  const replay = new Replay(recording)
  replay.seek(at)
  assert.equal(replay.output({ kind: 'model', source: 'cs2:game', epoch: 'record', spanId: 'aborted' }).text, 'Durable prefix')
})

test('validates selected asset sizes, nested references, and traversal without fetching', async () => {
  const trace = fixture()
  const value = trace.asset({ image: { $asset: 'assets/missing', mediaType: 'image/png', bytes: 10 } })
  trace.add({ type: 'instant', name: 'data.kept', track: 'city/events', attrs: {
    text: { ...value, mediaType: 'text/plain' }, json: value,
    bad: { $asset: 'assets/../escape', mediaType: 'text/plain', bytes: 0 },
    size: { ...value, bytes: value.bytes + 1 },
  } })
  trace.add({ type: 'source.stopped' })
  const recording = await loadRecording(trace.files())
  assert.equal(recording.issues.length, 3)
  assert.ok(recording.issues.some(issue => issue.message.includes('Missing asset')))
  assert.ok(recording.issues.some(issue => issue.message.includes('Unsafe')))
  assert.ok(recording.issues.some(issue => issue.message.includes('byte count')))
})

test('rejects unknown format, unsafe selected paths, and ambiguous recording folders', async () => {
  const trace = fixture()
  trace.records[0] = { source: 'cs2:game', epoch: 'record', clock: 'mono:record', seq: 0, ts: 0, type: 'source.started', format: 99, runtime: 'dotnet', profiles: [] }
  await assert.rejects(loadRecording(trace.files()), /Unsupported trace format/)
  await assert.rejects(loadRecording([trace.file('../records.jsonl', '')]), /Unsafe selected path/)
  await assert.rejects(loadRecording([trace.file('one/records.jsonl', ''), trace.file('two/records.jsonl', '')]), /exactly one session/)
})

test('official projections preserve root/background branches and causal links', async () => {
  const trace = fixture()
  trace.add({ type: 'span.begin', spanId: 'root', name: 'invoke_agent mayor', track: 'agents/mayor/turn', ids: { agent: 'mayor', agentSession: 'root-session' } })
  trace.add({ type: 'span.begin', spanId: 'child', name: 'invoke_agent inspector', track: 'agents/inspector/turn', ids: { agent: 'inspector', agentSession: 'child-session', originRun: 'root-run' }, links: [{ rel: 'caused-by', to: { spanId: 'root' } }] })
  trace.add({ type: 'span.end', spanId: 'root', track: 'agents/mayor/turn', status: 'cancelled' })
  trace.add({ type: 'span.end', spanId: 'child', track: 'agents/inspector/turn', status: 'ok' })
  trace.add({ type: 'source.stopped' })
  const recording = await loadRecording(trace.files())
  const timeline = recording.full.timelineView()
  const child = timeline.spans.get('cs2:game/record/child')!
  assert.equal(child.ids!.agentSession, 'child-session')
  assert.equal(spansOfLink(child.links[0], timeline, recording.full.reader.indexes)[0].spanId, 'root')
  assert.equal(timeline.spans.get('cs2:game/record/root')!.status, 'cancelled')
  assert.equal(recording.issues.length, 0)
})
