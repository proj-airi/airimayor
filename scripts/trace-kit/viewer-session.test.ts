import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ViewerSession } from './viewer-session'

test('session presentation keeps turns, native runs, model calls and tools distinct, with cursor-scoped evidence', async () => {
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
  assert.equal(summary.items.length, 3, 'the chat wrapper is a group, not another identical run bar')
  const run = summary.groups.find((group) => group.kind === 'run')!
  assert.equal(run.parent, summary.groups.find((group) => group.kind === 'turn')!.id)
  assert.equal(summary.groups.find((group) => group.kind === 'model')!.parent, run.id)
  assert.equal(summary.groups.find((group) => group.kind === 'tool')!.parent, run.id)
  assert.equal(
    summary.items.find((item) => item.kind === 'model')!.group,
    `${run.id}:model`,
    'native parentage groups calls even without an enriched run ID',
  )
  assert.match(summary.items.find((item) => item.kind === 'model')!.label, /Request 1.*model-a/)
  assert.equal(summary.items.find((item) => item.kind === 'tool')!.label, 'zone')
  const selection = summary.items.find((item) => item.kind === 'model')!.selection
  assert.equal(session.frame(2, selection, false).state, 'Not reached')
  assert.equal(session.frame(3, selection, false).state, 'In progress')
  const before = await session.evidence(selection, 3)
  assert.equal(JSON.stringify(before.sections).includes('model-a'), false)
  const end = session.frame(4, selection, false)
  assert.equal(end.state, 'ok')
  assert.match(end.breadcrumb.join('/'), /Turn 1.*Run 1.*Request 1/)
  assert.equal(JSON.stringify((await session.evidence(selection, 4)).sections).includes('model-a'), true)
})
