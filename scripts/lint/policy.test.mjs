import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const cli = resolve(root, 'node_modules/@alint-js/cli/dist/bin/index.mjs')

function inspect(file) {
  const result = spawnSync(process.execPath, [cli, 'config', 'inspect', file], {
    cwd: root,
    encoding: 'utf8',
    timeout: 30_000,
  })
  assert.equal(result.status, 0, result.stderr || result.error?.message)
  return result.stdout
}

test('native alint resolves all semantic policies at their intended seams', () => {
  const cases = [
    ['Agent/ClientSession.cs', ['root-cause', 'pre-release-shapes', 'shared-pipeline', 'deep-interface', 'reuse', 'current-contract', 'verification-authority', 'complete-retirement', 'city-contract', 'protocol-adapter']],
    ['Mod/CS2MCP/RequestHandlers.Build.cs', ['native-facts']],
    ['scripts/trace-kit/viewer.vue', ['ui-conventions']],
    ['docs/guide/maintained-example.md', ['current-contract', 'verification-authority']],
    ['MAINTAINING.md', ['current-contract']],
  ]
  for (const [file, rules] of cases) {
    const output = inspect(file)
    assert.match(output, /ignored: no/)
    for (const rule of rules) assert.ok(output.includes(`airimayor/${rule}: error`), `${file} must enable ${rule}`)
  }
})

test('native alint excludes frozen evidence, generated output and rule instructions', () => {
  for (const file of [
    'archive/Agent/Old.cs',
    'docs/adr/0001-in-process-meai-loop.md',
    'docs/research/2026-11-09-native-summary-runtime-facts.md',
    'docs/guide/2026-08-06-windows-onboarding.md',
    'Agent/obj/Generated.cs',
    'scripts/trace-kit/.upstream/index.ts',
    'scripts/lint/rules/root-cause/rule.alint.json',
  ]) {
    const output = inspect(file)
    assert.match(output, /ignored: yes/, file)
    assert.doesNotMatch(output, /airimayor\/[\w-]+: error/, file)
  }
})
