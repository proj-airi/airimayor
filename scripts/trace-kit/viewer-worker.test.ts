import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { Worker } from 'node:worker_threads'
import { proxy, releaseProxy, wrap } from 'comlink'
import nodeEndpoint from 'comlink/dist/umd/node-adapter'
import { build } from 'esbuild'
import type { ViewerSession } from './viewer-session'

test('the production recording worker imports cloned files, reports progress, and returns cursor-scoped frames over Comlink', async () => {
  const root = fileURLToPath(new URL('.', import.meta.url))
  const bundle = await build({
    entryPoints: [`${root}viewer-worker.ts`],
    bundle: true,
    write: false,
    platform: 'browser',
    format: 'iife',
    tsconfig: `${root}tsconfig.json`,
  })
  const require = createRequire(import.meta.url)
  const worker = new Worker(
    `
    const { parentPort } = require('node:worker_threads')
    Object.assign(globalThis, require(${JSON.stringify(require.resolve('comlink/dist/umd/node-adapter'))})(parentPort))
    ${bundle.outputFiles[0].text}
  `,
    { eval: true },
  )
  const remote = wrap<ViewerSession>(nodeEndpoint(worker))
  try {
    const records = [
      { type: 'source.started', format: 1, runtime: 'dotnet', profiles: [] },
      { type: 'span.begin', spanId: 'call', track: 'agents/mayor/llm', name: 'chat' },
      {
        type: 'instant',
        spanId: 'call',
        track: 'agents/mayor/llm',
        name: 'model.output.delta',
        attrs: { 'airimayor.data': { $asset: 'assets/delta.json', mediaType: 'application/json', bytes: 0 } },
      },
      { type: 'span.end', spanId: 'call', track: 'agents/mayor/llm', status: 'ok' },
      { type: 'source.stopped' },
    ]
    const delta = JSON.stringify({ text: 'Recorded output', contents: [] })
    records[2].attrs!['airimayor.data'].bytes = Buffer.byteLength(delta)
    const phases = new Set<string>()
    const summary = await remote.openFiles(
      [
        {
          path: 'test/records.jsonl',
          file: new Blob([
            records
              .map((record, seq) =>
                JSON.stringify({ source: 'test', epoch: 'test', clock: 'mono:test', seq, ts: seq * 10, ...record }),
              )
              .join('\n'),
          ]),
        },
        { path: 'test/assets/delta.json', file: new Blob([delta]) },
      ],
      proxy(async (progress) => {
        phases.add(progress.phase)
      }),
    )
    assert.deepEqual(summary.issues, [])
    assert.equal(summary.models, 1)
    assert.equal(
      summary.overview.length,
      4,
      'output chunks stay in replay and the complete log, rather than the overview',
    )
    assert.deepEqual([...phases], ['read', 'records', 'assets', 'timeline'])
    const selection = summary.defaultSelection!
    assert.equal((await remote.frame(1, selection, false)).text, '')
    assert.equal((await remote.frame(2, selection, false)).text, 'Recorded output')
    assert.equal((await remote.frame(1, selection, false)).text, '')
    assert.deepEqual((await remote.rawRecords([2]))[0].type, 'instant')
    const log = await remote.eventLog()
    assert.equal(log.entries.length, 5)
    assert.equal(log.dropped, 0)
    assert.deepEqual((await remote.locate({ kind: 'instant', key: log.entries[2].key })).selection, selection)
    const analysis = await remote.analyze()
    assert.equal(analysis.calls, 1)
    assert.equal(analysis.models[0].ttft, 10)
    assert.equal(analysis.usage.input.total, undefined)
    const search = await remote.search({ text: 'Recorded output', status: 'ok', limit: 1 })
    assert.equal(search.total, 1)
    assert.equal(search.hits[0].index, 2)
  } finally {
    remote[releaseProxy]()
    await worker.terminate()
  }
})
