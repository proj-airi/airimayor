import { createHash } from 'node:crypto'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = dirname(fileURLToPath(import.meta.url))
const upstream = JSON.parse(await readFile(join(root, 'upstream.json'), 'utf8'))
const destination = join(root, '.upstream')
const stamp = join(destination, 'commit')
const packages = ['trace-kit', 'stream-kit', 'timeline-canvas', 'ui-timeline', 'ui-trace', 'ui']
const expected = `${upstream.commit}\n${packages.join(',')}`
if ((await readFile(stamp, 'utf8').catch(() => '')) === expected) {
  console.log(`AIRI trace packages ${upstream.commit} already prepared`)
  process.exit(0)
}

async function download(url) {
  const response = await fetch(url, { headers: { 'User-Agent': 'airimayor-trace-check' } })
  if (!response.ok) throw new Error(`${response.status} downloading ${url}`)
  return response
}

const tree = await (
  await download(`https://api.github.com/repos/${upstream.repository}/git/trees/${upstream.commit}?recursive=1`)
).json()
if (tree.truncated) throw new Error('Upstream source tree is truncated')
const sourcePattern = new RegExp(`^packages/(${packages.join('|')})/(?:src/.+\\.(?:ts|vue|css)|package.json)$`)
const sources = tree.tree.filter(
  (entry) =>
    entry.type === 'blob' &&
    (sourcePattern.test(entry.path) || entry.path === 'packages/i18n/src/locales/en/tamagotchi/settings.yaml') &&
    !/\.(?:test|story)\.(?:ts|vue)$/.test(entry.path) &&
    !/\/(?:testing|stories)\//.test(entry.path),
)
if (!sources.length) throw new Error('No upstream trace-kit sources found')

let next = 0
await Promise.all(
  Array.from({ length: 6 }, async () => {
    while (next < sources.length) {
      const source = sources[next++]
      const path = join(destination, source.path)
      const existing = await readFile(path).catch(() => undefined)
      const hashOf = (bytes) => createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex')
      if (existing && hashOf(existing) === source.sha) continue
      const response = await download(
        `https://raw.githubusercontent.com/${upstream.repository}/${upstream.commit}/${source.path}`,
      )
      const bytes = Buffer.from(await response.arrayBuffer())
      const hash = hashOf(bytes)
      if (hash !== source.sha) throw new Error(`Git blob hash mismatch: ${source.path}`)
      await mkdir(dirname(path), { recursive: true })
      await writeFile(path, bytes)
    }
  }),
)
await writeFile(stamp, expected)
console.log(`Prepared ${sources.length} AIRI trace/UI sources at ${upstream.commit}`)
