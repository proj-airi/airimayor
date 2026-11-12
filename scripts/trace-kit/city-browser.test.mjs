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
  assert.match(await page.getByRole('region', { name: 'Replay controls', exact: true }).innerText(), /10 \/ 10/)
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
  await writeFile(join(directory, 'records.jsonl'), records.map((record) => JSON.stringify(record)).join('\n'))
  const browser = await chromium.launch({ headless: true })
  t.after(() => browser.close())
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } })
  page.setDefaultTimeout(10000)
  const errors = []
  page.on('pageerror', (error) => errors.push(error.message))
  await page.addInitScript(() => {
    window.cityChartFonts = new Map()
    window.cityGuideLines = new Map()
    const paths = new WeakMap()
    const prototype = CanvasRenderingContext2D.prototype
    const clear = prototype.clearRect,
      fill = prototype.fillText,
      begin = prototype.beginPath,
      move = prototype.moveTo,
      line = prototype.lineTo,
      stroke = prototype.stroke
    prototype.clearRect = function (...args) {
      window.cityChartFonts.set(this.canvas, [])
      window.cityGuideLines.set(this.canvas, [])
      return clear.apply(this, args)
    }
    prototype.beginPath = function (...args) {
      paths.set(this, [])
      return begin.apply(this, args)
    }
    prototype.moveTo = function (x, y) {
      paths.get(this)?.push([x, y])
      return move.call(this, x, y)
    }
    prototype.lineTo = function (x, y) {
      paths.get(this)?.push([x, y])
      return line.call(this, x, y)
    }
    prototype.stroke = function (...args) {
      const path = paths.get(this)
      if (path?.length === 2 && path[0][0] === path[1][0] && Math.abs(path[0][1] - path[1][1]) > 30)
        window.cityGuideLines.get(this.canvas)?.push({ dash: this.getLineDash(), color: this.strokeStyle })
      return stroke.apply(this, args)
    }
    prototype.fillText = function (...args) {
      window.cityChartFonts.get(this.canvas)?.push(this.font)
      return fill.apply(this, args)
    }
  })
  await page.goto(pathToFileURL(fileURLToPath(new URL('./viewer.html', import.meta.url))).href)
  await page.getByLabel('Select session record folder').setInputFiles(directory)
  await page.getByRole('tab', { name: 'City', exact: true }).click()
  const trend = page.getByRole('img', { name: 'Population trend', exact: true })
  await trend.waitFor()
  const canvas = trend.locator('canvas').first()
  await canvas.waitFor()
  const hoverBox = await canvas.boundingBox()
  await page.mouse.move(hoverBox.x + hoverBox.width * 0.6, hoverBox.y + hoverBox.height * 0.4)
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))))
  const guides = await canvas.evaluate((element) => window.cityGuideLines.get(element))
  assert.equal(guides.filter((guide) => guide.dash.length > 0).length, 0, 'hover must not add a second dashed cursor')
  const cursorGuides = await trend
    .locator('[data-city-playhead]')
    .evaluate((element) => window.cityGuideLines.get(element))
  assert.equal(
    cursorGuides.filter((guide) => guide.dash.length === 0 && guide.color === '#38bdf8').length,
    1,
    'Timeline draws the only playback cursor',
  )
  await page.mouse.move(0, 0)
  assert.equal(await trend.locator('svg').count(), 0)
  assert.equal(await page.getByRole('slider', { name: 'City time', exact: true }).count(), 0)
  assert.equal(await page.getByRole('button', { name: 'Return to replay', exact: true }).count(), 0)
  async function assertAxisLabelsFit() {
    const fonts = await canvas.evaluate((element) => window.cityChartFonts.get(element))
    assert.ok(fonts.length > 0, 'the chart library must render its axis labels')
    for (const font of fonts) assert.ok(Number.parseFloat(font.match(/([\d.]+)px/)[1]) <= 12, font)
  }
  const curvePixels = () =>
    canvas.evaluate((element) => {
      const { width, height } = element
      const pixels = element.getContext('2d').getImageData(0, 0, width, height).data
      const line = []
      for (let y = 0; y < height; y++) {
        for (let x = Math.floor(width * 0.5); x < width * 0.8; x++) {
          const at = (y * width + x) * 4
          if (pixels[at + 1] > pixels[at] + 20 && pixels[at + 2] > pixels[at] + 20 && pixels[at + 1] > 90) line.push(at)
        }
      }
      return line
    })
  await assertAxisLabelsFit()
  const fullCurve = await curvePixels()
  assert.ok(fullCurve.length > 0, 'the complete curve must remain visible ahead of replay')
  const cityControls = page.getByRole('group', { name: 'Population chart controls', exact: true })
  const scale = await cityControls.evaluate((element) => element.style.getPropertyValue('--ppm'))
  await page.mouse.move(hoverBox.x + hoverBox.width * 0.6, hoverBox.y + hoverBox.height * 0.4)
  await page.mouse.wheel(0, -100)
  await page.waitForFunction(
    (previous) =>
      document.querySelector('[aria-label="Population chart controls"]').style.getPropertyValue('--ppm') !== previous,
    scale,
  )
  await page.getByRole('button', { name: 'Fit city timeline', exact: true }).click()
  const positionBeforePan = await page.getByLabel('Replay time').inputValue()
  const viewBeforePan = await cityControls.evaluate((element) => element.style.getPropertyValue('--view-start'))
  await page.mouse.move(hoverBox.x + hoverBox.width * 0.6, hoverBox.y + hoverBox.height * 0.4)
  await page.mouse.down()
  await page.mouse.move(hoverBox.x + hoverBox.width * 0.5, hoverBox.y + hoverBox.height * 0.4, { steps: 5 })
  await page.mouse.up()
  await page.waitForFunction(
    (previous) =>
      document.querySelector('[aria-label="Population chart controls"]').style.getPropertyValue('--view-start') !==
      previous,
    viewBeforePan,
  )
  assert.equal(
    await page.getByLabel('Replay time').inputValue(),
    positionBeforePan,
    'panning uses Timeline view movement without seeking the recording',
  )
  await page.getByRole('button', { name: 'Fit city timeline', exact: true }).click()
  await page.mouse.move(0, 0)
  await canvas.evaluate((element) => {
    window.cityReplayCanvas = element
  })
  assert.equal(
    await page.getByRole('region', { name: 'Selected call', exact: true }).count(),
    0,
    'an empty inspector must not take dashboard width',
  )
  const chartBox = await trend.boundingBox(),
    footer = await page.getByLabel('Replay time').boundingBox()
  assert.ok(chartBox.y + chartBox.height < footer.y, 'the complete trend belongs in the first viewport')
  assert.equal(
    await page.getByRole('button', { name: 'Next readings', exact: true }).count(),
    0,
    'reading rows are secondary and initially collapsed',
  )
  const value = page.locator('[data-city-active-value]')
  await page.getByRole('slider', { name: 'City replay cursor', exact: true }).press('Home')
  await page.getByText('No reading reached at replay position', { exact: true }).waitFor()
  await page.getByRole('slider', { name: 'City replay cursor', exact: true }).press('End')
  await page.waitForFunction(() => document.querySelector('[data-city-active-value]').textContent.trim() === '18,000')
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
  assert.equal(
    await page.getByLabel('Trend range', { exact: true }).count(),
    0,
    'one acquired observation has no recent range to select',
  )
  await canvas.evaluate((element) => {
    window.cityReplayCanvas = element
  })
  const fixedCurve = await curvePixels()
  await page.getByRole('button', { name: 'First event', exact: true }).click()
  await page.getByText('No reading reached at replay position', { exact: true }).waitFor()
  assert.equal(await value.innerText(), '—', 'future readings must not populate the current value')
  assert.deepEqual(await curvePixels(), fixedCurve, 'rewinding must preserve the complete curve and fixed axes')
  assert.equal(await canvas.evaluate((element) => element === window.cityReplayCanvas), true)
  await page.getByRole('slider', { name: 'City replay cursor', exact: true }).press('End')
  await page.waitForFunction(() => document.querySelector('input[aria-label="Replay time"]').value !== '0')
  assert.equal(
    await page.getByRole('region', { name: 'Selected call', exact: true }).count(),
    0,
    'chart seeking must not open evidence',
  )
  await page.getByRole('button', { name: 'First event', exact: true }).click()
  await page.getByText('No reading reached at replay position', { exact: true }).waitFor()
  await canvas.click({ position: { x: (await canvas.boundingBox()).width * 0.6, y: 40 } })
  await page.waitForFunction(() => document.querySelector('input[aria-label="Replay time"]').value !== '0')
  assert.equal(await page.getByRole('region', { name: 'Selected call', exact: true }).count(), 0)
  await page.getByRole('button', { name: 'First event', exact: true }).click()
  await page.getByLabel('Playback speed', { exact: true }).selectOption('0.5')
  await page.getByRole('button', { name: 'Play', exact: true }).click()
  await page.waitForFunction(
    () =>
      Number(document.querySelector('[aria-label="City replay cursor"]').getAttribute('aria-valuenow')) > 5 &&
      document.querySelector('[title^="Event 1 of"]') !== null,
  )
  await page.waitForFunction(() => document.querySelector('[data-city-active-value]').textContent.trim() === '18,000')
  const pause = page.getByRole('button', { name: 'Pause', exact: true })
  if (await pause.count()) await pause.click()
  const cityCursor = Number(
    await page.getByRole('slider', { name: 'City replay cursor', exact: true }).getAttribute('aria-valuenow'),
  )
  await page.getByRole('tab', { name: 'Timeline', exact: true }).click()
  const timelineCursor = Number(
    await page.getByRole('slider', { name: 'Cursor', exact: true }).getAttribute('aria-valuenow'),
  )
  assert.ok(
    Math.abs(cityCursor - timelineCursor - 100) <= 1,
    `The cursor clock must match after the fixture's first trace event at 100ms: ${cityCursor}, ${timelineCursor}`,
  )
  await page.getByRole('tab', { name: 'City', exact: true }).click()
  assert.deepEqual(await curvePixels(), fixedCurve, 'playing must move the playhead without replacing the curve')
  assert.equal(await canvas.evaluate((element) => element === window.cityReplayCanvas), true)
  await page.getByRole('button', { name: 'Last event', exact: true }).click()
  await page.getByRole('button', { name: 'Open trend evidence', exact: true }).click()
  await page.getByRole('button', { name: 'Close selected call', exact: true }).click()
  assert.equal(await page.getByRole('region', { name: 'Selected call', exact: true }).count(), 0)
  await page.waitForFunction(
    () =>
      document.querySelector('input[aria-label="Replay time"]').value ===
      document.querySelector('input[aria-label="Replay time"]').max,
  )
  await page.getByRole('button', { name: 'First event', exact: true }).click()
  await page.getByText('No reading reached at replay position', { exact: true }).waitFor()
  await page.getByRole('button', { name: 'Last event', exact: true }).click()
  await trend.waitFor()
  await page.setViewportSize({ width: 390, height: 844 })
  await page.getByRole('tab', { name: 'City', exact: true }).click()
  await assertAxisLabelsFit()
  const mobileChart = await trend.boundingBox(),
    mobileFooter = await page.getByLabel('Replay time').boundingBox()
  assert.ok(
    mobileChart.y + mobileChart.height < mobileFooter.y - 25,
    'the mobile trend must be reachable without scrolling through KPI cards',
  )
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
  assert.deepEqual(errors, [])
})
