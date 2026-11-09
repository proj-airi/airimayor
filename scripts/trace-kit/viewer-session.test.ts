import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ViewerSession } from './viewer-session'
import { createTraceViewReader } from '@proj-airi/trace-kit/views'

test('AIRI view readers receive native nesting and cursor-scoped evidence without a parallel presentation model', async () => {
  const records: object[] = []
  const add = (record: object) => {
    records.push({
      source: 'game',
      epoch: 'session',
      clock: 'mono:session',
      seq: records.length,
      ts: records.length * 10,
      ...record,
    })
  }
  const ids = { turn: 'turn', run: 'run', agentSession: 'mayor', agent: 'mayor' }
  const modelIds = { turn: 'turn', agentSession: 'mayor', agent: 'mayor' }
  add({ type: 'source.started', format: 1, runtime: 'dotnet', profiles: [] })
  add({ type: 'span.begin', spanId: 'turn', track: 'agents/mayor/turn', name: 'chat-turn', ids: { turn: 'turn' } })
  add({ type: 'span.begin', spanId: 'run', track: 'agents/mayor/turn', name: 'invoke_agent mayor', ids })
  add({
    type: 'span.begin',
    spanId: 'model',
    parentSpanId: 'run',
    track: 'agents/mayor/llm',
    name: 'chat',
    ids: modelIds,
  })
  add({
    type: 'span.end',
    spanId: 'model',
    track: 'agents/mayor/llm',
    status: 'ok',
    attrs: { 'gen_ai.request.model': 'model-a' },
    ids: modelIds,
  })
  add({
    type: 'span.begin',
    spanId: 'tool',
    parentSpanId: 'run',
    track: 'agents/mayor/tools',
    name: 'execute_tool zone',
    ids,
  })
  add({
    type: 'span.end',
    spanId: 'tool',
    track: 'agents/mayor/tools',
    status: 'ok',
    attrs: { 'gen_ai.tool.name': 'zone' },
    ids,
  })
  add({ type: 'span.end', spanId: 'run', track: 'agents/mayor/turn', status: 'ok', ids })
  add({ type: 'span.end', spanId: 'turn', track: 'agents/mayor/turn', status: 'ok', ids: { turn: 'turn' } })
  add({ type: 'source.stopped' })
  const session = new ViewerSession()
  const summary = await session.openFiles(
    [{ path: 'session/records.jsonl', file: new Blob([records.map((record) => JSON.stringify(record)).join('\n')]) }],
    () => {},
  )
  assert.deepEqual(summary.issues, [])
  const view = createTraceViewReader(summary.clock, { seriesRetention: false })
  view.reader.handleRecords(summary.overview)
  const spans = [...view.timelineView().spans.values()]
  assert.equal(spans.length, 4)
  const run = spans.find((span) => span.name === 'invoke_agent mayor')!
  const model = spans.find((span) => span.track.endsWith('/llm'))!
  assert.equal(model.parentKey, run.key)
  const selection = summary.defaultSelection!
  assert.deepEqual(session.locate({ kind: 'span', key: model.key }), { position: 3, selection })
  assert.equal(session.frame(2, selection, false).state, 'Not reached')
  assert.equal(session.frame(3, selection, false).state, 'In progress')
  const before = await session.evidence(selection, 3)
  assert.equal(JSON.stringify(before.sections).includes('model-a'), false)
  const end = session.frame(4, selection, false)
  assert.equal(end.state, 'ok')
  assert.deepEqual(end.item, { kind: 'span', key: model.key })
  const prefix = createTraceViewReader(summary.clock, { seriesRetention: false })
  prefix.reader.handleRecords(session.snapshot(3))
  assert.equal(prefix.timelineView().spans.get(model.key)!.attrs['gen_ai.request.model'], undefined)
  assert.equal(session.eventLog().entries.length, records.length)
  assert.equal(session.eventLog().dropped, 0)
  assert.equal(JSON.stringify((await session.evidence(selection, 4)).sections).includes('model-a'), true)
})

test('offline AIRI event log retains records beyond the live projection limit without filling canvas rows with output chunks', async () => {
  const count = 20_001
  const records: object[] = [
    { type: 'source.started', format: 1, runtime: 'dotnet', profiles: [] },
    { type: 'span.begin', spanId: 'call', track: 'agents/mayor/llm', name: 'chat' },
    ...Array.from({ length: count }, () => ({
      type: 'instant',
      spanId: 'call',
      track: 'agents/mayor/llm',
      name: 'model.output.delta',
    })),
    { type: 'span.end', spanId: 'call', track: 'agents/mayor/llm', status: 'ok' },
    { type: 'source.stopped' },
  ]
  const session = new ViewerSession()
  const summary = await session.openFiles(
    [
      {
        path: 'test/records.jsonl',
        file: new Blob([
          records
            .map((record, seq) =>
              JSON.stringify({ source: 'test', epoch: 'test', clock: 'mono:test', seq, ts: seq, ...record }),
            )
            .join('\n'),
        ]),
      },
    ],
    () => {},
  )
  const log = session.eventLog()
  assert.equal(log.entries.length, count + 4)
  assert.equal(log.dropped, 0)
  assert.equal(summary.overview.length, 4)
  assert.equal(session.frame(summary.events.length - 1, summary.defaultSelection, false).chunks, count)
})
