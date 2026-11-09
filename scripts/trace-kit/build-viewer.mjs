import { build } from 'esbuild'
import { readFile, readdir, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = dirname(fileURLToPath(import.meta.url))
const worker = await build({
  entryPoints: [join(root, 'viewer-worker.ts')],
  bundle: true,
  write: false,
  minify: true,
  platform: 'browser',
  format: 'iife',
  target: 'es2022',
  tsconfig: join(root, 'tsconfig.json'),
  legalComments: 'inline',
  metafile: true,
})
const result = await build({
  entryPoints: [join(root, 'viewer.tsx')],
  bundle: true,
  write: false,
  minify: true,
  platform: 'browser',
  format: 'iife',
  target: 'es2022',
  tsconfig: join(root, 'tsconfig.json'),
  legalComments: 'inline',
  metafile: true,
  define: { __VIEWER_WORKER__: JSON.stringify(worker.outputFiles[0].text), 'process.env.NODE_ENV': '"production"' },
})
const template = await readFile(join(root, 'viewer-template.html'), 'utf8')
const css =
  (await readFile(join(root, 'node_modules/vis-timeline/styles/vis-timeline-graph2d.min.css'), 'utf8')) +
  '\n' +
  (await readFile(join(root, 'viewer.css'), 'utf8'))
const license = await readFile(join(root, 'LICENSE.upstream'), 'utf8')
const upstream = JSON.parse(await readFile(join(root, 'upstream.json'), 'utf8'))
const script = result.outputFiles[0].text.replaceAll('</script', '<\\/script')
const attribution = `<!-- trace-kit: https://github.com/${upstream.repository}/tree/${upstream.commit}/packages/trace-kit\n${license}\n-->\n`
const packages = new Set(
  [...Object.keys(result.metafile.inputs), ...Object.keys(worker.metafile.inputs)]
    .map((path) => path.match(/(?:^|\/)node_modules\/((?:@[^/]+\/)?[^/]+)/)?.[1])
    .filter(Boolean),
)
const licenses = await Promise.all(
  [...packages].sort().map(async (name) => {
    const directory = resolve(root, 'node_modules', name)
    const files = (await readdir(directory)).filter((file) => /^(?:licen[sc]e|notice)(?:\.|$)/i.test(file))
    if (!files.length) throw new Error(`Missing license text for bundled dependency ${name}`)
    return `${name}\n${(await Promise.all(files.map((file) => readFile(join(directory, file), 'utf8')))).join('\n')}`
  }),
)
const notices = `<!-- Bundled dependency licenses\n${licenses.join('\n\n').replaceAll('-->', '-- >')}\n-->\n`
const html = attribution + notices + template.replace('/* VIEWER_STYLE */', css).replace('/* VIEWER_SCRIPT */', script)
await writeFile(join(root, 'viewer.html'), html)
console.log(`Built offline viewer: ${join(root, 'viewer.html')} (${Buffer.byteLength(html).toLocaleString()} bytes)`)
