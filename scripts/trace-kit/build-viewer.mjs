import { build } from 'esbuild'
import { readFile, writeFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = dirname(fileURLToPath(import.meta.url))
const result = await build({
  entryPoints: [join(root, 'viewer.ts')], bundle: true, write: false,
  minify: true, platform: 'browser', format: 'iife', target: 'es2022',
  tsconfig: join(root, 'tsconfig.json'), legalComments: 'inline',
})
const template = await readFile(join(root, 'viewer-template.html'), 'utf8')
const css = await readFile(join(root, 'viewer.css'), 'utf8')
const license = await readFile(join(root, 'LICENSE.upstream'), 'utf8')
const upstream = JSON.parse(await readFile(join(root, 'upstream.json'), 'utf8'))
const script = result.outputFiles[0].text.replaceAll('</script', '<\\/script')
const attribution = `<!-- trace-kit: https://github.com/${upstream.repository}/tree/${upstream.commit}/packages/trace-kit\n${license}\n-->\n`
const html = attribution + template.replace('/* VIEWER_STYLE */', css).replace('/* VIEWER_SCRIPT */', script)
await writeFile(join(root, 'viewer.html'), html)
console.log(`Built offline viewer: ${join(root, 'viewer.html')} (${Buffer.byteLength(html).toLocaleString()} bytes)`)
