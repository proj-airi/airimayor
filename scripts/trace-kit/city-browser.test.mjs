import assert from 'node:assert/strict'
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { chromium } from 'playwright'

test('City prioritizes a line trend and supports road search, evidence, rewind and a narrow viewport', async (t) => {
  const directory = await mkdtemp(join(tmpdir(), 'airimayor-city-browser-'))
  await mkdir(join(directory, 'assets'))
  t.after(() => rm(directory, { recursive: true, force: true }))
  const records = [],
    files = []
  const add = (value) =>
    records.push({
      source: 'game',
      epoch: 'city',
      clock: 'mono:city',
      seq: records.length,
      ts: records.length * 100,
      ...value,
    })
  const tool = (name, frame, value, extra = {}) => {
    const data = JSON.stringify({
      tool: name,
      success: true,
      result: JSON.stringify(value),
      arguments: '{}',
      timeline: {
        applied: { frame, gameTime: `2026-01-01 ${String(Math.floor(frame / 1000)).padStart(2, '0')}:00` },
      },
      ...extra,
    })
    const path = `assets/${files.length}`
    files.push(writeFile(join(directory, path), data))
    add({
      type: 'instant',
      name: 'city.tool.result',
      track: 'agents/mayor/tools',
      attrs: {
        'airimayor.data': { $asset: path, mediaType: 'application/json', bytes: Buffer.byteLength(data) },
      },
    })
  }
  add({ type: 'source.started', format: 1, runtime: 'dotnet', profiles: [] })
  tool('get_labor', 1000, { employed: 5000, unemploymentRate: 7.5, homelessCitizens: 10 })
  tool('get_budget', 1000, { balance: 12000, totalIncome: 50000, totalExpenses: 38000 })
  tool('get_statistics', 10000, {
    type: 'Population',
    parameter: 0,
    current: 18000,
    frames: Array.from({ length: 80 }, (_, i) => 100 + i * 100),
    values: Array.from({ length: 80 }, (_, i) => 10000 + i * 90),
  })
  tool('list_networks', 10000, {
    kind: 'road',
    networks: Array.from({ length: 218 }, (_, i) => ({
      entity: { index: 72928 + i, version: 1 },
      prefab: 'Medium Road',
      traffic: { flowPercent: 40 + i / 10, volumeIndex: i, congestionIndex: i / 2, activeBottlenecks: 0 },
    })),
  })
  tool(
    'place_building',
    10000,
    {},
    {
      evidence: {
        before: { frame: 10000, paused: true, money: 1000000, buildings: 10 },
        after: { frame: 10000, paused: true, money: 900000, buildings: 11 },
      },
    },
  )
  tool('list_notifications', 10000, { total: 2, countsByType: { Traffic: 2 }, notifications: [] })
  add({ type: 'source.stopped' })
  await Promise.all(files)
  await writeFile(join(directory, 'records.jsonl'), records.map((record) => JSON.stringify(record)).join('\n'))
  const browser = await chromium.launch({ headless: true })
  t.after(() => browser.close())
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } })
  page.setDefaultTimeout(10000)
  const errors = []
  page.on('pageerror', (error) => errors.push(error.message))
  await page.goto(pathToFileURL(fileURLToPath(new URL('./viewer.html', import.meta.url))).href)
  await page.getByLabel('Select session record folder').setInputFiles(directory)
  await page.getByRole('tab', { name: 'City', exact: true }).click()
  const trend = page.getByRole('img', { name: 'Population trend', exact: true })
  await trend.waitFor()
  assert.ok(await trend.locator('polyline[data-city-line]').count(), 'City trends must join readings with a line')
  assert.equal(
    await page.getByRole('region', { name: 'Selected call', exact: true }).count(),
    0,
    'an empty inspector must not take dashboard width',
  )
  const chartBox = await trend.boundingBox(),
    footer = await page.getByLabel('Replay event position').boundingBox()
  assert.ok(chartBox.y + chartBox.height < footer.y, 'the complete trend belongs in the first viewport')
  assert.equal(
    await page.getByRole('button', { name: 'Next readings', exact: true }).count(),
    0,
    'reading rows are secondary and initially collapsed',
  )
  const value = page.locator('[data-city-active-value]')
  await page.getByRole('group', { name: 'Population chart controls', exact: true }).focus()
  await page.keyboard.press('ArrowLeft')
  assert.notEqual(await value.innerText(), '18,000', 'keyboard inspection reaches the previous recorded reading')
  await page.getByRole('combobox', { name: 'City measure', exact: true }).click()
  await page.getByRole('searchbox', { name: 'Search city measure', exact: true }).fill('unemployment')
  await page.keyboard.press('Enter')
  await page.getByRole('img', { name: 'Unemployment rate trend', exact: true }).waitFor()
  assert.equal(await value.innerText(), '7.5%')
  await page.getByRole('tab', { name: 'Finances', exact: true }).click()
  await page.getByRole('img', { name: 'Monthly balance trend', exact: true }).waitFor()
  await page.getByRole('tab', { name: 'Population', exact: true }).click()
  await page.getByRole('img', { name: 'Unemployment rate trend', exact: true }).waitFor()
  await page.getByRole('tab', { name: 'Metrics', exact: true }).click()
  await page.getByRole('tab', { name: 'City', exact: true }).click()
  await page.getByRole('img', { name: 'Unemployment rate trend', exact: true }).waitFor()
  await page.getByRole('tab', { name: 'Traffic', exact: true }).click()
  await page.getByRole('combobox', { name: 'Road', exact: true }).click()
  await page.getByRole('searchbox', { name: 'Search road', exact: true }).fill('72928:1')
  await page.getByRole('option').first().click()
  assert.match(await page.getByRole('combobox', { name: 'Road', exact: true }).innerText(), /72928:1/)
  await page.getByRole('combobox', { name: 'Road', exact: true }).click()
  await page.getByRole('searchbox', { name: 'Search road', exact: true }).fill('7292')
  await page.keyboard.press('ArrowDown')
  await page.keyboard.press('Enter')
  assert.match(await page.getByRole('combobox', { name: 'Road', exact: true }).innerText(), /72929:1/)
  await page.getByLabel('Traffic measure', { exact: true }).selectOption('congestionIndex')
  await page.getByRole('img', { name: 'Congestion trend', exact: true }).waitFor()
  await page.getByRole('tab', { name: 'Finances', exact: true }).click()
  await page.getByRole('button', { name: 'Population', exact: true }).click()
  await trend.waitFor()
  await page.getByLabel('Trend range', { exact: true }).selectOption('50')
  await page.waitForFunction(
    () => document.querySelector('[data-city-line]').getAttribute('points').split(' ').length === 50,
  )
  await page.getByLabel('City time', { exact: true }).focus()
  await page.keyboard.press('Home')
  await page.waitForFunction(() => document.querySelector('[data-city-active-value]').textContent.trim() === '10,000')
  await page.getByText('· outside the shown range', { exact: true }).waitFor()
  await page.getByRole('button', { name: 'Return to replay', exact: true }).click()
  await page.getByLabel('Trend range', { exact: true }).selectOption('all')
  await page.waitForFunction(
    () => document.querySelector('[data-city-line]').getAttribute('points').split(' ').length === 81,
  )
  await page.getByRole('button', { name: 'Previous city time', exact: true }).click()
  await page.getByRole('button', { name: 'Return to replay', exact: true }).click()
  await page.getByRole('button', { name: 'Open trend evidence', exact: true }).click()
  await page.getByRole('button', { name: 'Close city evidence', exact: true }).click()
  assert.equal(await page.getByRole('region', { name: 'Selected call', exact: true }).count(), 0)
  await page.waitForFunction(
    () =>
      document.querySelector('input[aria-label="Replay event position"]').value ===
      document.querySelector('input[aria-label="Replay event position"]').max,
  )
  await page.getByRole('button', { name: 'First event', exact: true }).click()
  await page.getByText('No city observations yet.', { exact: true }).waitFor()
  await page.getByRole('button', { name: 'Last event', exact: true }).click()
  await trend.waitFor()
  await page.setViewportSize({ width: 390, height: 844 })
  await page.getByRole('tab', { name: 'City', exact: true }).click()
  const mobileChart = await trend.boundingBox(),
    mobileFooter = await page.getByLabel('Replay event position').boundingBox()
  assert.ok(
    mobileChart.y + mobileChart.height < mobileFooter.y - 25,
    'the mobile trend must be reachable without scrolling through KPI cards',
  )
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
  assert.deepEqual(errors, [])
})
