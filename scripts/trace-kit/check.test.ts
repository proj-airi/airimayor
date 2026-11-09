import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { definitionRecordsOf } from '@proj-airi/trace-kit'
import { agentProfile } from '@proj-airi/trace-kit/agent'
import { checkRecord, Report } from './check'
import { profileDefinitions } from './profile'

const base = { source: 'cs2:game', epoch: 'session', clock: 'mono:session' }

test('embedded profile matches the pinned agent and mayor metric definitions', async () => {
  const embedded = JSON.parse(await readFile(new URL('../../Agent/TraceProfile.json', import.meta.url), 'utf8'))
  assert.deepEqual(embedded, profileDefinitions)
})

async function fixture(t: { after: (cleanup: () => Promise<void>) => void }) {
  const directory = await mkdtemp(join(tmpdir(), 'airimayor-trace-'))
  t.after(() => rm(directory, { recursive: true, force: true }))
  const records: object[] = []
  const messages: string[] = []
  const report = new Report(message => messages.push(message))
  const add = (record: object) => records.push({ ...base, seq: records.length, ts: records.length, ...record })
  add({ type: 'source.started', format: 1, runtime: 'unity', profiles: [{ key: 'agent', version: '1' }] })
  for (const definition of definitionRecordsOf(agentProfile, 0)) add(definition)
  const asset = async (value: unknown) => {
    const bytes = Buffer.from(JSON.stringify(value))
    const name = `${createHash('sha256').update(bytes).digest('hex')}.json`
    await mkdir(join(directory, 'assets'), { recursive: true })
    await writeFile(join(directory, 'assets', name), bytes)
    return { $asset: `assets/${name}`, mediaType: 'application/json', bytes: bytes.length }
  }
  const check = async () => {
    await writeFile(join(directory, 'records.jsonl'), `${records.map(record => JSON.stringify(record)).join('\n')}\n`)
    await checkRecord(directory, report)
    return { report, messages: messages.join('\n') }
  }
  return { directory, records, add, asset, check }
}

test('official projections replay model/tool spans and write evidence', async (t) => {
  const trace = await fixture(t)
  trace.add({ type: 'instant', name: 'session.fact', track: 'city/events', attrs: {
    'airimayor.data': await trace.asset({ key: 'session', value: { head: 'builtin', city: 'Test city', model: 'fixture' } }),
  } })
  trace.add({ type: 'instant', name: 'recording.gap', track: 'city/events', attrs: {
    'airimayor.data': await trace.asset({ message: 'provider-private fields unavailable' }),
  } })
  trace.add({ type: 'span.begin', spanId: 'model', name: 'chat', track: 'agents/mayor/llm' })
  trace.add({ type: 'span.end', spanId: 'model', status: 'ok', track: 'agents/mayor/llm', attrs: {
    'gen_ai.usage.input_tokens': 23, 'gen_ai.usage.output_tokens': 7,
  } })
  trace.add({ type: 'span.begin', spanId: 'call', name: 'execute_tool', track: 'agents/mayor/tools' })
  const evidence = {
    before: { paused: true, frame: 12, buildings: 0, money: 100 },
    after: { paused: true, frame: 12, buildings: 1, money: 90 },
  }
  trace.add({ type: 'instant', name: 'city.tool.result', spanId: 'call', track: 'agents/mayor/tools',
    attrs: { 'airimayor.data': await trace.asset({ tool: 'place_building', success: true, evidence }) } })
  trace.add({ type: 'span.end', spanId: 'call', status: 'ok', track: 'agents/mayor/tools' })
  trace.add({ type: 'source.stopped' })
  const result = await trace.check()
  assert.equal(result.report.failures, 0, result.messages)
  assert.equal(result.report.verdicts.confirmed, 1)
  assert.match(result.messages, /head builtin; city Test city; model fixture/)
  assert.match(result.messages, /known gap provider-private fields unavailable/)
  assert.match(result.messages, /1 model calls, 1 tools; tokens: input 23, output 7/)
})

test('preserves diverged and undetermined write verdicts', async (t) => {
  const trace = await fixture(t)
  const before = { paused: true, frame: 12, buildings: 0 }
  for (const [success, after] of [
    [false, { paused: true, frame: 12, buildings: 1 }],
    [true, { paused: false, frame: 13, buildings: 1 }],
  ] as const) {
    trace.add({ type: 'instant', name: 'city.tool.result', track: 'agents/mayor/tools',
      attrs: { 'airimayor.data': await trace.asset({ tool: 'place_building', success, evidence: { before, after } }) } })
  }
  trace.add({ type: 'source.stopped' })
  const result = await trace.check()
  assert.equal(result.report.failures, 1, result.messages)
  assert.equal(result.report.verdicts.diverged, 1)
  assert.equal(result.report.verdicts.undetermined, 1)
})

test('unchanged counters cannot disprove area expansion or road changes with a stable edge count', async (t) => {
  const trace = await fixture(t)
  const before = { paused: true, frame: 12, buildings: 533, networkEdges: 427, money: 100 }
  for (const [tool, after] of [
    ['expand_operational_area', { ...before }],
    ['build_network', { ...before, buildings: 532 }],
  ] as const) {
    trace.add({ type: 'instant', name: 'city.tool.result', track: 'agents/mayor/tools', attrs: {
      'airimayor.data': await trace.asset({ tool, success: true, evidence: { before, after } }),
    } })
  }
  trace.add({ type: 'source.stopped' })
  const result = await trace.check()
  assert.equal(result.report.failures, 0, result.messages)
  assert.equal(result.report.verdicts.confirmed, 0)
  assert.equal(result.report.verdicts.undetermined, 2)
  assert.match(result.messages, /ledger cannot confirm/)
})

test('rejects assets with wrong byte counts and paths outside the archive', async (t) => {
  const trace = await fixture(t)
  const ref = await trace.asset({ text: 'kept' })
  trace.add({ type: 'instant', name: 'asset.kept', track: 'agents/mayor/control', attrs: {
    broken: { ...ref, bytes: ref.bytes + 1 },
    outside: { $asset: 'assets/../../outside.json', mediaType: 'application/json', bytes: 0 },
    missing: { $asset: 'assets/missing.png', mediaType: 'image/png', bytes: 0 },
  } })
  trace.add({ type: 'source.stopped' })
  const result = await trace.check()
  assert.equal(result.report.failures, 3, result.messages)
  assert.match(result.messages, /reference says/)
  assert.match(result.messages, /unsafe asset path/)
  assert.match(result.messages, /missing.png/)
})

test('validates asset references nested inside JSON assets', async (t) => {
  const trace = await fixture(t)
  const data = await trace.asset({ tool: 'zone', success: true, files: [
    { name: 'image.png', asset: { $asset: 'assets/missing.png', mediaType: 'image/png', bytes: 12 } },
  ] })
  trace.add({ type: 'instant', name: 'asset.kept', track: 'city/events', attrs: {
    text: { ...data, mediaType: 'text/plain' },
  } })
  trace.add({ type: 'instant', name: 'city.tool.result', track: 'agents/mayor/tools', attrs: { 'airimayor.data': data } })
  trace.add({ type: 'source.stopped' })
  const result = await trace.check()
  assert.equal(result.report.failures, 1, result.messages)
  assert.equal(result.report.verdicts.undetermined, 1)
})

test('rejects unknown format and legacy records', async (t) => {
  const trace = await fixture(t)
  trace.records[0] = { ...base, seq: 0, ts: 0, type: 'source.started', format: 99, runtime: 'unity', profiles: [] }
  trace.records.push({ schema: 1, seq: 1, type: 'function', data: {} })
  trace.add({ type: 'source.stopped' })
  const result = await trace.check()
  assert.match(result.messages, /format 99 is not supported/)
  assert.match(result.messages, /invalid native trace record/)
  assert.ok(result.report.failures >= 2)
})

test('rejects incomplete source and open spans', async (t) => {
  const trace = await fixture(t)
  trace.add({ type: 'span.begin', spanId: 'child', name: 'chat', track: 'agents/inspector/llm' })
  const result = await trace.check()
  assert.equal(result.report.failures, 2, result.messages)
  assert.match(result.messages, /no source.stopped/)
  assert.match(result.messages, /started and never ended/)
})

test('rejects orphan span ends and sequence gaps', async (t) => {
  const trace = await fixture(t)
  trace.add({ type: 'span.end', spanId: 'unknown', status: 'cancelled', track: 'agents/mayor/llm' })
  trace.add({ type: 'source.stopped', seq: trace.records.length + 1 })
  const result = await trace.check()
  assert.equal(result.report.failures, 2, result.messages)
  assert.match(result.messages, /has no open begin/)
  assert.match(result.messages, /does not follow/)
})
