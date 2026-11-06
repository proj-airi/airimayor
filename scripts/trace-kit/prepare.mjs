import { createHash } from 'node:crypto'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = dirname(fileURLToPath(import.meta.url))
const upstream = JSON.parse(await readFile(join(root, 'upstream.json'), 'utf8'))
const destination = join(root, '.upstream')
const stamp = join(destination, 'commit')
if (await readFile(stamp, 'utf8').catch(() => '') === upstream.commit) {
  console.log(`trace-kit ${upstream.commit} already prepared`)
  process.exit(0)
}

async function download(url) {
  const response = await fetch(url, { headers: { 'User-Agent': 'airimayor-trace-check' } })
  if (!response.ok)
    throw new Error(`${response.status} downloading ${url}`)
  return response
}

const tree = await (await download(
  `https://api.github.com/repos/${upstream.repository}/git/trees/${upstream.commit}?recursive=1`,
)).json()
if (tree.truncated)
  throw new Error('Upstream source tree is truncated')
const sources = tree.tree.filter(entry => entry.type === 'blob'
  && /^packages\/(trace-kit|stream-kit)\/src\/.+\.ts$/.test(entry.path)
  && !entry.path.endsWith('.test.ts')
  && !entry.path.includes('/testing/'))
if (!sources.length)
  throw new Error('No upstream trace-kit sources found')

let next = 0
await Promise.all(Array.from({ length: 6 }, async () => {
  while (next < sources.length) {
    const source = sources[next++]
    const response = await download(
      `https://raw.githubusercontent.com/${upstream.repository}/${upstream.commit}/${source.path}`,
    )
    const bytes = Buffer.from(await response.arrayBuffer())
    const hash = createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex')
    if (hash !== source.sha)
      throw new Error(`Git blob hash mismatch: ${source.path}`)
    const path = join(destination, source.path)
    await mkdir(dirname(path), { recursive: true })
    await writeFile(path, bytes)
  }
}))
await writeFile(stamp, upstream.commit)
console.log(`Prepared ${sources.length} trace-kit/stream-kit sources at ${upstream.commit}`)
