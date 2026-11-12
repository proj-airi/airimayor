import assert from 'node:assert/strict'
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { chromium } from 'playwright'

test('request focus scopes interleaved replay and metrics, with an inspector that opens only when selected', async (t) => {
  const directory = await mkdtemp(join(tmpdir(), 'airimayor-viewer-browser-'))
  t.after(() => rm(directory, { recursive: true, force: true }))
  await mkdir(join(directory, 'assets'))
  const records = []
  const add = (value) =>
    records.push({
      source: 'game',
      epoch: 'turns',
      clock: 'mono:turns',
      seq: records.length,
      ts: records.length * 100,
      ...value,
    })
  add({ type: 'source.started', format: 1, runtime: 'dotnet', profiles: [] })
  for (const turn of ['first', 'second']) {
    const text = JSON.stringify({
      user: `Build ${turn} district. ${'Keep essential services, city finances and traffic working as the population grows. '.repeat(20)}`,
    })
    await writeFile(join(directory, 'assets', turn), text)
    const ids = { turn, agent: 'mayor' }
    add({
      type: 'span.begin',
      spanId: turn,
      name: 'chat-turn',
      track: 'agents/mayor/turn',
      ids,
      attrs: {
        'airimayor.data': {
          $asset: `assets/${turn}`,
          mediaType: 'application/json',
          bytes: Buffer.byteLength(text),
        },
      },
    })
    add({
      type: 'span.begin',
      spanId: `${turn}-run`,
      name: 'invoke_agent mayor',
      track: 'agents/mayor/turn',
      ids,
    })
    add({
      type: 'span.begin',
      spanId: `${turn}-model`,
      parentSpanId: `${turn}-run`,
      name: `chat ${turn}-model`,
      track: 'agents/mayor/llm',
      ids,
    })
    add({
      type: 'instant',
      spanId: `${turn}-model`,
      name: 'model.output.delta',
      track: 'agents/mayor/llm',
      ids,
      attrs: { 'airimayor.data': JSON.stringify({ index: 0, text: `${turn} answer` }) },
    })
    add({
      type: 'span.end',
      spanId: `${turn}-model`,
      track: 'agents/mayor/llm',
      status: 'ok',
      ids,
      attrs: { 'gen_ai.usage.input_tokens': turn === 'first' ? 100 : 200 },
    })
    if (turn === 'first') {
      const data = JSON.stringify({
        tool: 'get_budget',
        success: true,
        result: JSON.stringify({ balance: 10000, totalIncome: 200, totalExpenses: 100 }),
        timeline: { applied: { frame: 100, gameTime: '2026-01-01 00:00' } },
      })
      await writeFile(join(directory, 'assets', 'budget'), data)
      add({
        type: 'instant',
        name: 'city.tool.result',
        track: 'agents/mayor/tools',
        ids,
        attrs: {
          'airimayor.data': {
            $asset: 'assets/budget',
            mediaType: 'application/json',
            bytes: Buffer.byteLength(data),
          },
        },
      })
    }
    add({ type: 'instant', name: 'stop.requested', track: 'city/events', ids })
    add({ type: 'span.end', spanId: `${turn}-run`, track: 'agents/mayor/turn', status: 'ok', ids })
    add({ type: 'span.end', spanId: turn, track: 'agents/mayor/turn', status: 'ok', ids })
  }
  add({
    type: 'span.complete',
    spanId: 'child',
    name: 'background inspection',
    track: 'agents/inspector/llm',
    durationMs: 1,
    status: 'ok',
    ids: { originTurn: 'first', agent: 'inspector' },
  })
  add({ type: 'source.stopped' })
  await writeFile(
    join(directory, 'records.jsonl'),
    records.map((record) => JSON.stringify(record)).join('\n'),
  )
  const browser = await chromium.launch({ headless: true })
  t.after(() => browser.close())
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } })
  page.setDefaultTimeout(10000)
  const errors = []
  page.on('pageerror', (error) => errors.push(error.message))
  await page.goto(pathToFileURL(fileURLToPath(new URL('./viewer.html', import.meta.url))).href)
  await page.getByLabel('Select session record folder').setInputFiles(directory)
  const scope = page.getByRole('combobox', { name: /Recording scope/ })
  await scope.waitFor()
  assert.equal(await scope.innerText(), 'Turn 2')
  assert.notEqual(
    await scope.locator('[aria-hidden="true"]').evaluate((element) => getComputedStyle(element).maskImage),
    'none',
    'the shared select arrow must be included in the offline build',
  )
  await scope.click()
  const scopeMenu = page.getByRole('listbox')
  await scopeMenu.waitFor()
  const scopeBox = await scopeMenu.boundingBox()
  assert.ok(scopeBox.x >= 0 && scopeBox.x + scopeBox.width <= 1280, 'the turn menu must fit inside the page')
  assert.equal(await scopeMenu.getByText('Keep essential services', { exact: false }).count(), 0)
  await page.keyboard.press('Escape')
  await page.getByText('Mayor turns', { exact: true }).waitFor()
  await page.getByText('Session events', { exact: true }).waitFor()
  const inspector = page.getByRole('region', { name: 'Selected call', exact: true })
  assert.equal(await inspector.count(), 0)
  const timeline = page.getByRole('region', { name: 'Session trace', exact: true })
  const fullWidth = (await timeline.boundingBox()).width
  await page.getByRole('button', { name: /chat second-model/ }).press('Enter')
  await inspector.waitFor()
  assert.ok((await timeline.boundingBox()).width < fullWidth)
  await page.getByText('second answer', { exact: true }).waitFor()
  await page.getByRole('button', { name: 'Close selected call', exact: true }).click()
  assert.equal(await inspector.count(), 0)
  await page.getByRole('tab', { name: 'City', exact: true }).click()
  await page.getByRole('tab', { name: 'Finances', exact: true }).click()
  await page.getByRole('img', { name: 'Monthly balance trend', exact: true }).waitFor()
  await page.getByRole('button', { name: 'Open trend evidence', exact: true }).click()
  await inspector.waitFor()
  assert.equal(await scope.innerText(), 'Whole session')
  await page.getByRole('button', { name: 'Close selected call', exact: true }).click()
  await scope.filter({ hasText: 'Turn 2' }).waitFor()
  await page.getByRole('tab', { name: 'Timeline', exact: true }).click()
  await scope.click()
  await scopeMenu.getByRole('option', { name: 'Turn 1', exact: true }).click()
  await page.getByRole('button', { name: /chat first-model/ }).waitFor()
  assert.equal(await page.getByRole('button', { name: /chat second-model/ }).count(), 0)
  // The late child belongs to the first turn; intervening second-turn events never enter its playback positions.
  const slider = page.getByLabel('Replay event position')
  assert.equal(await slider.getAttribute('max'), '9')
  await page.getByRole('tab', { name: 'Metrics', exact: true }).click()
  await page.getByText('2 model calls', { exact: true }).waitFor()
  const metricsNav = page.getByRole('navigation', { name: 'Metrics sections', exact: true })
  const metricsReport = page.getByRole('region', { name: 'Metrics report', exact: true })
  const metricsScroll = metricsReport.locator('[data-reka-scroll-area-viewport]')
  async function openSection(name, title) {
    const link = metricsNav.getByRole('link', { name, exact: true })
    await link.press('Enter')
    assert.equal(await link.getAttribute('aria-current'), 'location')
    const heading = await metricsReport.getByRole('heading', { name: title, exact: true }).boundingBox()
    const report = await metricsReport.boundingBox()
    assert.ok(
      Math.abs(heading.y - report.y - 12) < 2,
      `${name} must scroll to the start of the report viewport`,
    )
    assert.ok(await metricsNav.isVisible(), 'section navigation stays reachable after scrolling')
  }
  assert.ok(
    (await metricsNav.boundingBox()).x < (await metricsReport.boundingBox()).x,
    'wide panels show section navigation on the left',
  )
  await openSection('Context', 'Context and cache by request')
  await metricsReport
    .getByRole('button', { name: 'mayor: input 100, cache unavailable', exact: true })
    .press('Enter')
  await inspector.waitFor()
  await page.setViewportSize({ width: 900, height: 800 })
  await page.waitForFunction(() => {
    const nav = document.querySelector('nav[aria-label="Metrics sections"]').getBoundingClientRect()
    const report = document.querySelector('[aria-label="Metrics report"]').getBoundingClientRect()
    return nav.bottom <= report.top
  })
  const splitNav = await metricsNav.boundingBox()
  const splitReport = await metricsReport.boundingBox()
  assert.ok(
    splitNav.y + splitNav.height <= splitReport.y,
    `a narrow report pane uses top navigation even inside a desktop workbench: ${JSON.stringify({ splitNav, splitReport })}`,
  )
  await page.getByRole('button', { name: 'Close selected call', exact: true }).click()
  await page.setViewportSize({ width: 1280, height: 800 })
  await page.waitForFunction(
    () => document.querySelector('[aria-label="Metrics report"]').getBoundingClientRect().width > 1000,
  )
  await openSection('Tools', 'Tool execution')
  const metricsPosition = await metricsScroll.evaluate((element) => element.scrollTop)
  await page.getByRole('tab', { name: 'Timeline', exact: true }).click()
  await page.getByRole('tab', { name: 'Metrics', exact: true }).click()
  assert.equal(await metricsScroll.evaluate((element) => element.scrollTop), metricsPosition)
  assert.equal(
    await metricsNav.getByRole('link', { name: 'Tools', exact: true }).getAttribute('aria-current'),
    'location',
  )
  await metricsReport.hover()
  await page.mouse.wheel(0, 10000)
  await metricsNav.locator('a[aria-current="location"]').filter({ hasText: 'Distributions' }).waitFor()
  await page.mouse.wheel(0, -10000)
  await metricsNav.locator('a[aria-current="location"]').filter({ hasText: 'Overview' }).waitFor()
  await page.getByRole('tab', { name: 'Timeline', exact: true }).click()
  await scope.click()
  await scopeMenu.getByRole('option', { name: 'Whole session', exact: true }).click()
  await page.getByRole('button', { name: /chat second-model/ }).waitFor()
  await page.setViewportSize({ width: 390, height: 844 })
  await scope.press('Enter')
  await scopeMenu.waitFor()
  const narrowScopeBox = await scopeMenu.boundingBox()
  assert.ok(narrowScopeBox.x >= 0 && narrowScopeBox.x + narrowScopeBox.width <= 390)
  await page.keyboard.press('Escape')
  assert.equal(await scope.innerText(), 'Whole session')
  await page.getByRole('tab', { name: 'Metrics', exact: true }).click()
  const narrowNav = await metricsNav.boundingBox()
  const narrowReport = await metricsReport.boundingBox()
  assert.ok(
    narrowNav.y + narrowNav.height <= narrowReport.y,
    'narrow panels put section navigation above the report',
  )
  await openSection('Tools', 'Tool execution')
  await openSection('Overview', 'Overview')
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
  await page.getByRole('tab', { name: 'Timeline', exact: true }).click()
  await page.getByRole('button', { name: /chat second-model/ }).press('Enter')
  await inspector.waitFor()
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
  const footer = await page.getByRole('region', { name: 'Replay controls' }).boundingBox()
  assert.ok(footer.y + footer.height <= 844, 'playback stays in the viewport while inspecting a call')
  await page.getByRole('button', { name: 'Close selected call', exact: true }).click()
  await timeline.waitFor()
  assert.deepEqual(errors, [])
})

test('selecting a split model call draws one continuous summary ring and one detail ring', async (t) => {
  const directory = await mkdtemp(join(tmpdir(), 'airimayor-ring-browser-'))
  t.after(() => rm(directory, { recursive: true, force: true }))
  const records = [
    { type: 'source.started', ts: 0, format: 1, runtime: 'dotnet', profiles: [] },
    {
      type: 'definition',
      ts: 0,
      kind: 'track',
      key: 'agents/{agent}/llm',
      data: {
        path: 'agents/{agent}/llm',
        entity: 'agent',
        group: 'agents',
        render: ['spans', 'instants'],
        activity: 'thinking',
        activitySplit: { instant: 'llm.first_chunk', activity: 'active' },
        label: 'Model',
        color: '#a855f7',
      },
    },
    { type: 'span.begin', ts: 163855.931, spanId: 'model', name: 'chat split-model', track: 'agents/mayor/llm' },
    { type: 'instant', ts: 167148.1694, spanId: 'model', name: 'llm.first_chunk', track: 'agents/mayor/llm' },
    { type: 'span.end', ts: 209063.5076, spanId: 'model', track: 'agents/mayor/llm', status: 'ok' },
    { type: 'source.stopped', ts: 576103.1 },
  ].map((record, seq) => ({ source: 'game', epoch: 'rings', clock: 'mono:rings', seq, ...record }))
  await writeFile(join(directory, 'records.jsonl'), records.map((record) => JSON.stringify(record)).join('\n'))
  const browser = await chromium.launch({ headless: true })
  t.after(() => browser.close())
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } })
  page.setDefaultTimeout(10000)
  await page.addInitScript(() => {
    const paths = new WeakMap()
    const frames = new Map()
    window.traceRingFrames = frames
    const prototype = CanvasRenderingContext2D.prototype
    const clear = prototype.clearRect,
      begin = prototype.beginPath,
      rounded = prototype.roundRect,
      stroke = prototype.stroke
    prototype.clearRect = function (...args) {
      frames.set(this.canvas, [])
      return clear.apply(this, args)
    }
    prototype.beginPath = function (...args) {
      paths.delete(this)
      return begin.apply(this, args)
    }
    prototype.roundRect = function (x, y, width, height, ...args) {
      paths.set(this, { x, y, width, height })
      return rounded.call(this, x, y, width, height, ...args)
    }
    prototype.stroke = function (...args) {
      const path = paths.get(this)
      if (path && this.strokeStyle === '#38bdf8' && this.lineWidth === 2)
        frames.get(this.canvas)?.push({ ...path, alpha: this.globalAlpha })
      return stroke.apply(this, args)
    }
  })
  await page.goto(pathToFileURL(fileURLToPath(new URL('./viewer.html', import.meta.url))).href)
  await page.getByLabel('Select session record folder').setInputFiles(directory)
  await page.getByRole('button', { name: /chat split-model/ }).press('Enter')
  await page.getByRole('button', { name: 'Fit timeline', exact: true }).click()
  await page.waitForFunction(() =>
    [...window.traceRingFrames.values()].flat().some((ring) => Math.abs(ring.alpha - 0.85) < 0.01),
  )
  const rings = await page.evaluate(() => [...window.traceRingFrames.values()].flat())
  const summary = rings.filter((ring) => Math.abs(ring.alpha - 0.85) < 0.01)
  const detail = rings.filter((ring) => Math.abs(ring.alpha - 1) < 0.01)
  assert.equal(summary.length, 1, 'adjacent phases of the selected call must share one summary outline')
  assert.equal(detail.length, 1, 'the detail row keeps its own selection outline')
  assert.ok(Math.abs(summary[0].x - detail[0].x) < 1)
  assert.ok(Math.abs(summary[0].width - detail[0].width) < 1, 'the summary outline covers the complete call')
})

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
  await writeFile(
    join(directory, 'records.jsonl'),
    records.map((record) => JSON.stringify(record)).join('\n'),
  )
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
  async function assertAxisLabelsFit() {
    const labels = await trend.locator('text').evaluateAll((elements) =>
      elements.map((element) => {
        const bounds = element.getBoundingClientRect()
        const chart = element.ownerSVGElement.getBoundingClientRect()
        return {
          text: element.textContent.trim(),
          fontSize: Number.parseFloat(getComputedStyle(element).fontSize),
          fits:
            bounds.left >= chart.left && bounds.right <= chart.right &&
            bounds.top >= chart.top && bounds.bottom <= chart.bottom,
        }
      }),
    )
    assert.ok(labels.length > 0)
    for (const label of labels) {
      assert.ok(label.fontSize <= 12, `Axis label "${label.text}" is too large at ${label.fontSize}px`)
      assert.ok(label.fits, `Axis label "${label.text}" must fit inside the chart`)
    }
  }
  await assertAxisLabelsFit()
  assert.ok(
    await trend.locator('polyline[data-city-line]').count(),
    'City trends must join readings with a line',
  )
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
  assert.notEqual(
    await value.innerText(),
    '18,000',
    'keyboard inspection reaches the previous recorded reading',
  )
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
  await page.getByRole('listbox').getByRole('option').first().click()
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
  await page.waitForFunction(
    () => document.querySelector('[data-city-active-value]').textContent.trim() === '10,000',
  )
  await page.getByText('· outside the shown range', { exact: true }).waitFor()
  await page.getByRole('button', { name: 'Return to replay', exact: true }).click()
  await page.getByLabel('Trend range', { exact: true }).selectOption('all')
  await page.waitForFunction(
    () => document.querySelector('[data-city-line]').getAttribute('points').split(' ').length === 81,
  )
  await page.getByRole('button', { name: 'Previous city time', exact: true }).click()
  await page.getByRole('button', { name: 'Return to replay', exact: true }).click()
  await page.getByRole('button', { name: 'Open trend evidence', exact: true }).click()
  await page.getByRole('button', { name: 'Close selected call', exact: true }).click()
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
  await assertAxisLabelsFit()
  const mobileChart = await trend.boundingBox(),
    mobileFooter = await page.getByLabel('Replay event position').boundingBox()
  assert.ok(
    mobileChart.y + mobileChart.height < mobileFooter.y - 25,
    'the mobile trend must be reachable without scrolling through KPI cards',
  )
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
  assert.deepEqual(errors, [])
})
