import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createServer } from 'node:http'
import { mkdir, mkdtemp, readdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const cli = resolve(root, 'node_modules/@alint-js/cli/dist/bin/index.mjs')
const plugin = resolve(root, 'scripts/lint/rules').replaceAll('\\', '/')

function run(cwd, args) {
  return new Promise((resolveRun, reject) => {
    const child = spawn(process.execPath, [cli, ...args], { cwd, windowsHide: true })
    let stdout = ''
    let stderr = ''
    child.stdout.on('data', chunk => { stdout += chunk })
    child.stderr.on('data', chunk => { stderr += chunk })
    child.on('error', reject)
    child.on('close', status => resolveRun({ status, stdout, stderr }))
  })
}

test('native semantic rules report findings and fail on an unavailable provider', { timeout: 60_000 }, async (t) => {
  const fixture = await mkdtemp(resolve(tmpdir(), 'airimayor semantic lint '))
  t.after(() => rm(fixture, { recursive: true, force: true }))
  let unavailable = false
  let requests = 0
  const server = createServer(async (request, response) => {
    let body = ''
    for await (const chunk of request) body += chunk
    requests++
    if (unavailable) {
      response.writeHead(503, { 'content-type': 'application/json' })
      response.end(JSON.stringify({ error: { message: 'Fixture provider is unavailable.' } }))
      return
    }
    const input = JSON.parse(body)
    const report = input.tools.find(tool => tool.function.name === 'report_findings')
    const argumentsJson = JSON.stringify({ findings: [{ line: 2, message: 'Fixture contract violation.', confidence: 'high' }] })
    if (input.stream) {
      response.writeHead(200, { 'content-type': 'text/event-stream' })
      for (const chunk of [
        { choices: [{ index: 0, delta: { role: 'assistant', tool_calls: [{ index: 0, id: `report-${requests}`, type: 'function', function: { name: report.function.name, arguments: argumentsJson } }] }, finish_reason: null }] },
        { choices: [{ index: 0, delta: {}, finish_reason: 'tool_calls' }], usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 } },
      ]) response.write(`data: ${JSON.stringify(chunk)}\n\n`)
      response.end('data: [DONE]\n\n')
      return
    }
    response.writeHead(200, { 'content-type': 'application/json' })
    response.end(JSON.stringify({
      id: `fixture-${requests}`,
      object: 'chat.completion',
      model: 'fixture',
      choices: [{ index: 0, finish_reason: 'tool_calls', message: {
        role: 'assistant', content: null,
        tool_calls: [{ id: `report-${requests}`, type: 'function', function: {
          name: report.function.name,
          arguments: argumentsJson,
        } }],
      } }],
      usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 },
    }))
  })
  await new Promise(resolveListen => server.listen(0, '127.0.0.1', resolveListen))
  t.after(() => new Promise(resolveClose => server.close(resolveClose)))
  const names = await readdir(plugin)
  const rules = Object.fromEntries(names.map(name => [`airimayor/${name}`, 'error']))
  await mkdir(resolve(fixture, 'Agent'))
  await mkdir(resolve(fixture, '.alint'))
  await writeFile(resolve(fixture, 'Agent/Example.cs'), 'class Example\n{\n}\n')
  await writeFile(resolve(fixture, 'alint.config.json'), JSON.stringify({ config: { group: [{
    files: ['Agent/*.cs'], language: 'plaintext', plugins: { airimayor: plugin }, rules,
  }] } }))
  await writeFile(resolve(fixture, '.alint/config.toml'), `version = 1
[[providers]]
id = "fixture"
type = "openai-compatible"
endpoint = "http://127.0.0.1:${server.address().port}/v1/"
[[providers.models]]
id = "fixture"
aliases = ["default"]
capabilities = ["tool-call"]
[runner]
rule_concurrency = 1
timeout_ms = 15000
`)
  const install = await run(fixture, ['plugin', 'install'])
  assert.equal(install.status, 0, install.stderr)
  const findings = await run(fixture, ['Agent/Example.cs', '--format', 'json', '--no-stats'])
  assert.equal(findings.status, 1, findings.stderr)
  const result = JSON.parse(findings.stdout)
  assert.equal(result.execution.completed, names.length)
  assert.equal(result.execution.failed, 0)
  assert.equal(result.diagnostics.length, names.length)
  assert.deepEqual(new Set(result.diagnostics.map(diagnostic => diagnostic.ruleId)), new Set(Object.keys(rules)))
  assert.ok(result.diagnostics.every(diagnostic => diagnostic.loc.start.line === 2))
  assert.equal(requests, names.length)
  unavailable = true
  const failure = await run(fixture, ['Agent/Example.cs', '--rule', 'airimayor/root-cause', '--format', 'json', '--no-stats'])
  assert.notEqual(failure.status, 0)
  assert.equal(JSON.parse(failure.stdout).execution.failed, 1)
  assert.match(failure.stderr, /503/)
})
