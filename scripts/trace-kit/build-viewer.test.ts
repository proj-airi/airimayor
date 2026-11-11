import assert from 'node:assert/strict'
import { execFile } from 'node:child_process'
import { readFile } from 'node:fs/promises'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import { Script } from 'node:vm'

test('the final offline HTML contains one syntactically valid viewer script', async () => {
  await promisify(execFile)(process.execPath, [fileURLToPath(new URL('./build-viewer.mjs', import.meta.url))])
  const html = await readFile(new URL('./viewer.html', import.meta.url), 'utf8')
  assert.match(html, /480 Design/)
  assert.match(html, /https:\/\/creativecommons.org\/licenses\/by\/4.0\//)
  const css = html.match(/<style>([\s\S]*?)<\/style>/)![1]
  assert.doesNotMatch(css, /theme\(|%alpha/)
  const scripts = [...html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script\s*>/gi)]
  assert.equal(scripts.length, 1)
  try {
    new Script(scripts[0][1], { filename: 'viewer-inline.js' })
  } catch (error) {
    assert.fail(`The embedded viewer script is invalid: ${(error as Error).message}`)
  }
})
