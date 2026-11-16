import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import MarkdownIt from 'markdown-it'

const sourceRoots = ['Agent', 'Mod', 'Mcp', 'Mod.Tests', 'scripts/trace-kit']
const excludedDirectories = new Set(['node_modules', 'bin', 'obj', 'build', 'dist', 'vendor', 'upstream'])
const sourceExtension = /\.(?:cs|csproj|[cm]?js|tsx?|vue|scss|json)$/
const retiredIdentifier = /\b(?:zone_area|zone_rectangle|build_road|set_tax|set_fee|set_service_budget|set_loan|create_transit_line|delete_transit_line|get_operational_area|count_zone_cells|probe_cell_layer|find_placement|find_infrastructure_candidate|list_roads|list_pipes|list_cables|CitiesSkylines2Agent|LOCAL_MAP|map_export)\b/g
const cjk = /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Hangul}]/u

function filesUnder(root, directory) {
  if (directory === 'Mod/UI/types') return []
  const path = join(root, directory)
  if (!existsSync(path)) return []
  return readdirSync(path, { withFileTypes: true }).flatMap(entry => {
    if (entry.name.startsWith('.') || excludedDirectories.has(entry.name) || entry.isSymbolicLink()) return []
    const file = `${directory}/${entry.name}`
    return entry.isDirectory() ? filesUnder(root, file) : [file]
  })
}

function mask(text) {
  return text.replace(/"{3,}[\s\S]*?"{3,}|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\/\*[\s\S]*?\*\/|\/\/[^\r\n]*/g,
    part => part.replace(/[^\r\n]/g, ' '))
}

function documentParts(text) {
  const parts = { links: [], prose: [] }
  let quoteDepth = 0
  let line = 0
  for (const token of new MarkdownIt().parse(text, {})) {
    if (token.map) line = token.map[0]
    if (token.type === 'blockquote_open') quoteDepth++
    if (token.type === 'blockquote_close') quoteDepth--
    if (token.type !== 'inline') continue
    const offset = text.split('\n').slice(0, line).reduce((sum, sourceLine) => sum + sourceLine.length + 1, 0)
    for (const child of token.children) {
      if (child.type === 'link_open' || child.type === 'image') {
        parts.links.push({ target: child.attrGet(child.type === 'image' ? 'src' : 'href'), offset })
      }
      if (quoteDepth === 0 && child.type === 'text') {
        parts.prose.push({ text: child.content.replace(/"[^"\n]*"|“[^”\n]*”/g, ''), offset })
      }
    }
  }
  return parts
}

function currentDocument(file) {
  return /^(?:[^/]+\.md|docs\/[^/]+\.md)$/.test(file)
    || (file.startsWith('docs/guide/') && file.endsWith('.md') && !file.split('/').some(part => /^\d{4}-\d{2}-\d{2}-/.test(part)))
}

export function checkRepository(root) {
  root = resolve(root)
  const diagnostics = []
  const read = file => readFileSync(join(root, file), 'utf8')
  const report = (rule, file, message, offset = 0) => {
    diagnostics.push({ rule, file, line: read(file).slice(0, offset).split('\n').length, message })
  }
  const sources = sourceRoots.flatMap(directory => filesUnder(root, directory)).filter(file =>
    sourceExtension.test(file) && !/(?:package-lock|upstream)\.json$/.test(file))

  for (const file of sources) {
    const text = read(file)
    if (file.startsWith('Agent/')) {
      const code = file.endsWith('.cs') ? mask(text) : text
      if (file.endsWith('.cs')) {
        const gameReference = /\b(?:global::)?(?:Game|Unity\w*|Colossal|CS2MCP|airimayor\.Host)\s*\.|^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?(?:Game|Unity\w*|Colossal|CS2MCP|airimayor\.Host)\s*;/gm
        for (const match of code.matchAll(gameReference)) {
          report('agent-independence', file, 'Keep game assemblies and host implementation behind IAgentTools.', match.index)
        }
      }
      else {
        const xml = code.replace(/<!--[\s\S]*?-->/g, part => part.replace(/[^\r\n]/g, ' '))
        for (const match of xml.matchAll(/<(Reference|PackageReference|ProjectReference)\b[^>]*\bInclude\s*=\s*["']([^"']+)["'][^>]*(?:\/>|>[\s\S]*?<\/\1\s*>)/g)) {
          if (/\b(?:Game|Unity\w*|Colossal|CS2MCP)\b|(?:^|[/\\])Mod[/\\]|\bairimayor(?:\.dll|\.csproj|["',])/i.test(match[0])) {
            report('agent-independence', file, 'Keep game assemblies and host implementation behind IAgentTools.', match.index)
          }
        }
      }
    }
    if (!/\.test\.[^/]+$|Tests\.cs$/.test(file)) {
      for (const match of text.matchAll(retiredIdentifier)) {
        report('retired-tools', file, `Remove retired identifier ${match[0]} from active source (ADRs 0003, 0009, 0027, 0035).`, match.index)
      }
    }
    if (file.startsWith('Mod/UI/src/')) {
      const uncommented = text.replace(/\/\*[\s\S]*?\*\/|\/\/[^\r\n]*/g, part => part.replace(/[^\r\n]/g, ' '))
      if (file.endsWith('.scss')) {
        for (const match of uncommented.matchAll(/\bvar\s*\(/g)) {
          report('ui-conventions', file, 'Use literal Gameface colors; CSS var() is unsupported (docs/guide/ui-icons.md).', match.index)
        }
      }
      for (const match of uncommented.matchAll(/(?:from\s*|@?import\s*)["'](?:@unocss|unocss)[^"']*["']/g)) {
        report('ui-conventions', file, 'Use Gameface styles here; UnoCSS belongs to the offline viewer.', match.index)
      }
    }
  }

  const catalogFile = 'Mod/Host/ToolCatalog.json'
  const catalogText = read(catalogFile)
  let catalog
  try { catalog = JSON.parse(catalogText) }
  catch (error) { report('catalog-contract', catalogFile, `Invalid tool catalog JSON: ${error.message}`) }
  if (catalog !== undefined) {
    if (!catalog || !Array.isArray(catalog.tools)) {
      report('catalog-contract', catalogFile, 'The tool catalog must contain a tools array.')
    }
    else {
      const routes = new Set([...read('Mod/CS2MCP/RequestHandlers.cs').matchAll(/\bcase\s+"([^"\r\n]+)"\s*:/g)].map(match => match[1]))
      const hostFile = 'Mod/Host/Cs2AgentTools.cs'
      const host = existsSync(join(root, hostFile)) ? read(hostFile) : ''
      const directPanel = /if\s*\(\s*string\.Equals\s*\(name,\s*"panel"[^)]*\)\s*\)\s*\{[^}]*\bPanelCommand\.RunAsync\s*\(/s.test(host)
      const names = new Set()
      let searchOffset = 0
      for (const tool of catalog.tools) {
        if (!tool || typeof tool !== 'object' || Array.isArray(tool)) {
          report('catalog-contract', catalogFile, 'Each tool must be a declaration object.')
          continue
        }
        const offset = catalogText.indexOf(JSON.stringify(tool.name), searchOffset)
        searchOffset = Math.max(offset, searchOffset) + 1
        const problem = message => report('catalog-contract', catalogFile, `${tool.name}: ${message}`, Math.max(offset, 0))
        if (typeof tool.name !== 'string' || !/^[a-z][a-z0-9_]*$/.test(tool.name)) problem('Use a snake_case tool name.')
        if (names.has(tool.name)) problem('Duplicate tool name.')
        names.add(tool.name)
        const properties = tool.parameters?.properties ?? {}
        for (const key of tool.parameters?.required ?? []) {
          if (!Object.hasOwn(properties, key)) problem(`Required argument ${key} is absent from properties.`)
        }
        for (const query of tool.query ?? []) {
          if (query.arg && !Object.hasOwn(properties, query.arg)) problem(`Query argument ${query.arg} is absent from properties.`)
        }
        if (!['json', 'text', 'png'].includes(tool.response ?? 'json')) problem(`Unsupported response kind ${tool.response}.`)
        if (!routes.has(tool.route) && !(tool.name === 'panel' && directPanel)) problem(`Route ${tool.route} has no bridge or host dispatch.`)
        const descriptions = value => {
          if (!value || typeof value !== 'object') return
          for (const [key, child] of Object.entries(value)) {
            if (key === 'description' && typeof child === 'string' && cjk.test(child)) {
              report('english-prose', catalogFile, `${tool.name}: write model-facing descriptions in English.`, Math.max(catalogText.indexOf(JSON.stringify(child)), 0))
            }
            else if (child && typeof child === 'object') descriptions(child)
          }
        }
        descriptions(tool)
      }
    }
  }

  const docs = [...filesUnder(root, 'docs'), ...readdirSync(root).filter(file => file.endsWith('.md'))]
    .filter(file => file.endsWith('.md'))
  const indexFile = 'docs/README.md'
  const indexLinks = documentParts(read(indexFile)).links.map(({ target }) => resolve(root, 'docs', decodeURIComponent(target.split('#')[0])))
  const adrs = filesUnder(root, 'docs/adr')
  const numbers = new Set()
  for (const file of adrs) {
    const name = file.slice('docs/adr/'.length)
    const match = /^(\d{4})-[a-z0-9]+(?:-[a-z0-9]+)*\.md$/.exec(name)
    if (!match) { report('documentation', file, 'ADRs use sequential NNNN-slug.md filenames.'); continue }
    const number = Number(match[1])
    if (numbers.has(number)) report('documentation', file, `Duplicate ADR number ${match[1]}.`)
    numbers.add(number)
    if (!indexLinks.includes(resolve(root, file))) {
      report('documentation', file, `Link this ADR from ${indexFile}.`)
    }
  }
  const highest = Math.max(0, ...numbers)
  for (let number = 1; number <= highest; number++) {
    if (!numbers.has(number)) report('documentation', indexFile, `ADR sequence is missing ${String(number).padStart(4, '0')}.`)
  }
  for (const file of docs) {
    if (/(?:^|\/)open-work\.md$/i.test(file) && file !== 'docs/open-work.md') {
      report('documentation', file, 'Keep the current unfinished inventory only in docs/open-work.md.')
    }
    if (!currentDocument(file) && !file.startsWith('docs/adr/')) continue
    const text = read(file)
    const { prose, links } = documentParts(text)
    for (const part of prose) {
      if (cjk.test(part.text)) report('english-prose', file, 'Write current documentation in English; localization and quotations are separate.', part.offset)
    }
    for (const link of links) {
      const target = link.target.split('#')[0]
      if (!target || /^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(target)) continue
      const destination = resolve(root, dirname(file), decodeURIComponent(target))
      if (!existsSync(destination)) {
        report('documentation', file, `Relative link target does not exist: ${target}.`, link.offset)
      }
    }
  }
  const promptFile = 'Mod/Host/AgentSystemPrompt.cs'
  if (existsSync(join(root, promptFile))) {
    const text = read(promptFile)
    for (const match of text.matchAll(/[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Hangul}]+/gu)) {
      report('english-prose', promptFile, 'Write the city system prompt in English.', match.index)
    }
  }
  return diagnostics.sort((left, right) => left.file.localeCompare(right.file) || left.line - right.line || left.rule.localeCompare(right.rule))
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length > 2) throw new Error('npm run lint checks repository-wide contracts and accepts no file arguments; use alint for scoped semantic review.')
    const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
    const diagnostics = checkRepository(root)
    for (const diagnostic of diagnostics) {
      console.error(`${diagnostic.file}:${diagnostic.line}:1 error ${diagnostic.rule}: ${diagnostic.message}`)
    }
    console.log(`Repository contracts: ${diagnostics.length ? `${diagnostics.length} errors` : 'passed'}. Semantic review: npm run lint:ai.`)
    process.exitCode = diagnostics.length ? 1 : 0
  }
  catch (error) {
    console.error(`Repository lint could not complete: ${error.message}`)
    process.exitCode = 2
  }
}
