import assert from 'node:assert/strict'
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import test from 'node:test'
import { checkRepository } from './check.mjs'

function cityTool() {
  return {
    name: 'read_city',
    description: 'Read the current city.',
    parameters: { type: 'object', properties: {}, required: [] },
    route: '/city/overview',
    query: [],
    response: 'json',
  }
}

async function repository(t) {
  const root = await mkdtemp(join(tmpdir(), 'airimayor lint '))
  t.after(() => rm(root, { recursive: true, force: true }))
  const write = async (file, text) => {
    const path = join(root, file)
    await mkdir(dirname(path), { recursive: true })
    await writeFile(path, text, 'utf8')
  }
  const catalog = async (tools) => write('Mod/Host/ToolCatalog.json', JSON.stringify({ tools }, null, 2))
  const files = {
    'README.md': '# AIRI Mayor\n\n[Documentation](docs/README.md).\n',
    'Agent/airimayor.Agent.csproj': '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net48;net10.0</TargetFrameworks></PropertyGroup></Project>\n',
    'Agent/City.cs': 'using System;\nnamespace airimayor.Agent { public class City { } }\n',
    'Mod/CS2MCP/RequestHandlers.cs': 'switch (path) { case "/city/overview": return ReadCity(); }\n',
    'Mod/Host/AgentSystemPrompt.cs': 'const string Prompt = @"Read the city before changing it.";\n',
    'Mod/UI/src/mods/chat/chat.module.scss': '// Gameface drops var() declarations.\n.button { color: #ffffff; }\n',
    'docs/README.md': '# Docs index\n\n[Native city](adr/0001-native-city.md).\n[Open work](open-work.md).\n',
    'docs/adr/0001-native-city.md': '# Native city\n\nStatus: accepted\n\nUse native city validation.\n',
    'docs/open-work.md': '# Open work\n\n## Not implemented\n\n## Awaiting live acceptance\n',
    'scripts/trace-kit/build-viewer.mjs': 'import { createGenerator, presetIcons } from "unocss"\n',
  }
  for (const [file, content] of Object.entries(files)) await write(file, content)
  await catalog([cityTool()])
  return { root, write, catalog, check: () => checkRepository(root) }
}

function hasFinding(findings, rule, file) {
  return findings.some((finding) => finding.rule === rule && finding.file.replaceAll('\\', '/') === file)
}

test('accepts a coherent repository rooted in a path with spaces', async (t) => {
  const repo = await repository(t)
  await repo.write('Mod/LocaleZhHans.cs', 'public string Title = "城市";\n')
  await repo.write('docs/guide/current.md', '# Example\n\n```text\n城市\n```\n')
  await repo.write('archive/Agent/Old.cs', 'using Game.Simulation; // find_placement\n')
  await repo.write('Agent/bin/Generated.cs', 'using Unity.Entities;\n')
  await repo.write('Mod/UI/node_modules/example/index.ts', 'export const old = "list_roads";\n')
  assert.deepEqual(await repo.check(), [])
})

test('finds Agent game dependencies in imports and qualified names', async (t) => {
  const repo = await repository(t)
  await repo.write('Agent/Imported.cs', '// A native dependency appears below.\nusing Game.Simulation;\nnamespace airimayor.Agent { }\n')
  await repo.write('Agent/Qualified.cs', 'namespace airimayor.Agent\n{\n    class State { Unity.Entities.Entity value; }\n}\n')
  const findings = await repo.check()
  assert.ok(hasFinding(findings, 'agent-independence', 'Agent/Imported.cs'))
  assert.ok(hasFinding(findings, 'agent-independence', 'Agent/Qualified.cs'))
  assert.equal(findings.find((finding) => finding.file === 'Agent/Imported.cs').line, 2)
  assert.equal(findings.find((finding) => finding.file === 'Agent/Qualified.cs').line, 3)
  for (const finding of findings) {
    assert.ok(Number.isInteger(finding.line) && finding.line > 0)
    assert.ok(typeof finding.message === 'string' && finding.message.length > 0)
  }
})

test('finds a game assembly dependency declared only in the Agent project', async (t) => {
  const repo = await repository(t)
  await repo.write('Agent/airimayor.Agent.csproj', '<Project><ItemGroup><Reference Include="Game"><HintPath>../Game.dll</HintPath></Reference></ItemGroup></Project>\n')
  assert.ok(hasFinding(await repo.check(), 'agent-independence', 'Agent/airimayor.Agent.csproj'))
})

test('permits a field named Game and a commented game assembly reference', async (t) => {
  const repo = await repository(t)
  await repo.write('Agent/City.cs', 'namespace airimayor.Agent { public class City { public string Game; } }\n')
  await repo.write('Agent/airimayor.Agent.csproj', '<Project><ItemGroup><!-- <Reference Include="Game"><HintPath>../Game.dll</HintPath></Reference> --></ItemGroup></Project>\n')
  assert.deepEqual(await repo.check(), [])
})

test('rejects a host assembly reference resolved through its HintPath', async (t) => {
  const repo = await repository(t)
  await repo.write('Agent/airimayor.Agent.csproj', '<Project><ItemGroup><Reference Include="airimayor"><HintPath>../Mod/bin/airimayor.dll</HintPath></Reference></ItemGroup></Project>\n')
  assert.ok(hasFinding(await repo.check(), 'agent-independence', 'Agent/airimayor.Agent.csproj'))
})

test('checks handwritten Agent source in a directory named types', async (t) => {
  const repo = await repository(t)
  await repo.write('Agent/types/Native.cs', 'using Game.Simulation;\nnamespace airimayor.Agent { }\n')
  assert.ok(hasFinding(await repo.check(), 'agent-independence', 'Agent/types/Native.cs'))
})

test('rejects ambiguous duplicate tool declarations', async (t) => {
  const repo = await repository(t)
  await repo.catalog([cityTool(), cityTool()])
  assert.ok(hasFinding(await repo.check(), 'catalog-contract', 'Mod/Host/ToolCatalog.json'))
})

test('rejects unsupported catalog roots and null tool declarations', async (t) => {
  const repo = await repository(t)
  for (const text of ['null', 'false', '0', '{"tools":[null]}']) {
    await repo.write('Mod/Host/ToolCatalog.json', text)
    assert.ok(hasFinding(await repo.check(), 'catalog-contract', 'Mod/Host/ToolCatalog.json'))
  }
})

test('keeps only one undated unfinished inventory while permitting frozen audits', async (t) => {
  const repo = await repository(t)
  await repo.write('docs/ops/2026-11-16-open-work.md', '# Frozen audit\n')
  assert.deepEqual(await repo.check(), [])
  await repo.write('docs/ops/open-work.md', '# Another current inventory\n')
  assert.ok(hasFinding(await repo.check(), 'documentation', 'docs/ops/open-work.md'))
})

test('checks catalog argument and response contracts independently', async (t) => {
  const changes = {
    'required argument absent from properties': (tool) => { tool.parameters.required = ['district'] },
    'query argument absent from properties': (tool) => { tool.query = [{ key: 'district', arg: 'district' }] },
    'unsupported response kind': (tool) => { tool.response = 'jpeg' },
  }
  for (const [name, change] of Object.entries(changes)) {
    await t.test(name, async (t) => {
      const repo = await repository(t)
      const tool = cityTool()
      change(tool)
      await repo.catalog([tool])
      assert.ok(hasFinding(await repo.check(), 'catalog-contract', 'Mod/Host/ToolCatalog.json'))
    })
  }
})

test('checks that a catalog route reaches a dispatch case', async (t) => {
  const repo = await repository(t)
  const tool = cityTool()
  tool.route = '/city/missing'
  await repo.catalog([tool])
  assert.ok(hasFinding(await repo.check(), 'catalog-contract', 'Mod/Host/ToolCatalog.json'))
})

test('does not mistake an image caption branch for host dispatch', async (t) => {
  const repo = await repository(t)
  const tool = cityTool()
  tool.route = '/city/missing'
  await repo.catalog([tool])
  await repo.write('Mod/Host/Cs2AgentTools.cs', 'private static string ImageCaption(string name) { if (string.Equals(name, "read_city", StringComparison.Ordinal)) return "City overview."; return null; }\n')
  assert.ok(hasFinding(await repo.check(), 'catalog-contract', 'Mod/Host/ToolCatalog.json'))
})

test('accepts the panel tool dispatched directly by the host', async (t) => {
  const repo = await repository(t)
  const tool = cityTool()
  tool.name = 'panel'
  tool.route = '/panel'
  await repo.catalog([tool])
  await repo.write('Mod/Host/Cs2AgentTools.cs', `
if (string.Equals(name, "panel", StringComparison.Ordinal))
{
    PanelResult panel = await PanelCommand.RunAsync(argumentsJson, cancellationToken);
    return new AgentToolResult { Success = panel.Ok, Text = panel.Text };
}
`)
  assert.deepEqual(await repo.check(), [])
})

test('permits literal query parameters without a model argument', async (t) => {
  const repo = await repository(t)
  const tool = cityTool()
  tool.query = [{ key: 'scope', literal: 'city' }]
  await repo.catalog([tool])
  assert.deepEqual(await repo.check(), [])
})

test('finds retired names in active handlers without treating history as active guidance', async (t) => {
  const repo = await repository(t)
  await repo.write('Mod/CS2MCP/Retired.cs', 'const string RemovedTool = "find_placement";\n')
  await repo.write('docs/research/2026-11-16-retired-tool.md', '# Earlier tools\n\nStatus: frozen\n\nfind_placement was removed.\n')
  await repo.write('docs/adr/0001-native-city.md', '# Native city\n\nStatus: accepted\n\nReplace find_placement with native placement.\n')
  const findings = await repo.check()
  assert.ok(hasFinding(findings, 'retired-tools', 'Mod/CS2MCP/Retired.cs'))
  assert.ok(!findings.some((finding) => finding.rule === 'retired-tools' && finding.file.startsWith('docs/')))
})

test('checks current English prose while allowing localized resources and code examples', async (t) => {
  const repo = await repository(t)
  await repo.write('README.md', '# AIRI Mayor\n\n城市规划。\n')
  await repo.write('Mod/LocaleZhHans.cs', 'public string Title = "城市";\n')
  await repo.write('docs/guide/current.md', '# Example\n\n```text\n城市\n```\n')
  const findings = await repo.check()
  assert.ok(hasFinding(findings, 'english-prose', 'README.md'))
  assert.ok(!hasFinding(findings, 'english-prose', 'Mod/LocaleZhHans.cs'))
  assert.ok(!hasFinding(findings, 'english-prose', 'docs/guide/current.md'))
})

test('checks the prose of an indexed ADR even though it records a past decision', async (t) => {
  const repo = await repository(t)
  await repo.write('docs/adr/0001-native-city.md', '# Native city\n\nStatus: accepted\n\n使用原生城市验证。\n')
  assert.ok(hasFinding(await repo.check(), 'english-prose', 'docs/adr/0001-native-city.md'))
})

test('allows quoted historical Chinese within an otherwise English ADR', async (t) => {
  const repo = await repository(t)
  await repo.write('docs/adr/0001-native-city.md', '# Native city\n\nStatus: accepted\n\nThe earlier request said:\n\n> 使用原生城市验证。\n\nUse native city validation.\n')
  assert.deepEqual(await repo.check(), [])
})

test('checks ADR filenames, sequence, and index membership', async (t) => {
  const cases = {
    'unnumbered ADR': ['docs/adr/native-choice.md', 'docs/adr/native-choice.md'],
    'gap in ADR numbering': ['docs/adr/0003-other-choice.md', 'docs/README.md'],
    'ADR absent from the index': ['docs/adr/0002-other-choice.md', 'docs/adr/0002-other-choice.md'],
  }
  for (const [name, [file, expectedFile]] of Object.entries(cases)) {
    await t.test(name, async (t) => {
      const repo = await repository(t)
      await repo.write(file, '# Native choice\n')
      if (name === 'gap in ADR numbering') {
        await repo.write('docs/README.md', '# Docs index\n\n[First](adr/0001-native-city.md).\n[Third](adr/0003-other-choice.md).\n')
      }
      assert.ok(hasFinding(await repo.check(), 'documentation', expectedFile))
    })
  }
})

test('resolves current relative links with escaped spaces and fragments', async (t) => {
  const repo = await repository(t)
  await repo.write('docs/guide/current guide.md', '# Current guide\n')
  await repo.write('README.md', '# AIRI Mayor\n\n[Guide](docs/guide/current%20guide.md#current-guide).\n[Website](https://example.com/missing).\n')
  assert.deepEqual(await repo.check(), [])
  await repo.write('README.md', '# AIRI Mayor\n\n[Missing](docs/guide/missing.md).\n')
  assert.ok(hasFinding(await repo.check(), 'documentation', 'README.md'))
})

test('checks angle-bracket Markdown link targets containing spaces', async (t) => {
  const repo = await repository(t)
  await repo.write('docs/guide/current guide.md', '# Current guide\n')
  await repo.write('README.md', '# AIRI Mayor\n\n[Guide](<docs/guide/current guide.md>).\n')
  assert.deepEqual(await repo.check(), [])
  await repo.write('README.md', '# AIRI Mayor\n\n[Missing](<docs/guide/missing guide.md>).\n')
  assert.ok(hasFinding(await repo.check(), 'documentation', 'README.md'))
})

test('accepts a Markdown reference link as ADR index membership', async (t) => {
  const repo = await repository(t)
  await repo.write('docs/README.md', '# Docs index\n\n[One][decision]\n\n[decision]: ./adr/0001-native-city.md\n')
  assert.deepEqual(await repo.check(), [])
})

test('rejects Gameface CSS variables while permitting literal styles and explanatory comments', async (t) => {
  const repo = await repository(t)
  assert.deepEqual(await repo.check(), [])
  await repo.write('Mod/UI/src/mods/chat/chat.module.scss', '// Do not use var() in this host.\n.button { color: var(--primary); }\n')
  assert.ok(hasFinding(await repo.check(), 'ui-conventions', 'Mod/UI/src/mods/chat/chat.module.scss'))
})

test('keeps UnoCSS in Trace and out of the Gameface host', async (t) => {
  const repo = await repository(t)
  assert.deepEqual(await repo.check(), [])
  await repo.write('Mod/UI/src/mods/chat/utility.ts', 'import "@unocss/reset/tailwind.css";\n')
  assert.ok(hasFinding(await repo.check(), 'ui-conventions', 'Mod/UI/src/mods/chat/utility.ts'))
})
