import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ViewerSession } from './viewer-session'

function fixture() {
  const records: object[] = [],
    assets: { path: string; file: Blob }[] = []
  const add = (record: object) => {
    const index = records.length
    records.push({ source: 'game', epoch: 'city', clock: 'mono:city', seq: index, ts: index * 10, ...record })
    return index
  }
  const asset = (value: unknown) => {
    const file = new Blob([JSON.stringify(value)]),
      path = `assets/${assets.length}`
    assets.push({ path: `city/${path}`, file })
    return { $asset: path, mediaType: 'application/json', bytes: file.size }
  }
  const stamp = (frame: number) => ({ frame, gameTime: `2026-01-01 ${frame}:00`, speed: 0 })
  const tool = (name: string, frame: number | undefined, result: unknown, extra: object = {}) =>
    add({
      type: 'instant',
      name: 'city.tool.result',
      track: 'agents/mayor/tools',
      attrs: {
        'airimayor.data': asset({
          tool: name,
          success: true,
          arguments: '{}',
          result: JSON.stringify(result),
          timeline: frame === undefined ? {} : { applied: stamp(frame), completed: stamp(frame) },
          ...extra,
        }),
      },
    })
  const start = add({ type: 'source.started', format: 1, runtime: 'dotnet', profiles: [] })
  const open = async () => {
    add({ type: 'source.stopped' })
    const session = new ViewerSession()
    const summary = await session.openFiles(
      [
        { path: 'city/records.jsonl', file: new Blob([records.map((record) => JSON.stringify(record)).join('\n')]) },
        ...assets,
      ],
      () => {},
    )
    assert.deepEqual(summary.issues, [])
    return { session, end: summary.events.length - 1 }
  }
  return { add, asset, stamp, tool, open, start, assets }
}

test('historical city samples become visible only when acquired, deduplicate overlapping frames, and retain native provenance', async () => {
  const trace = fixture()
  const first = trace.tool('get_statistics', 10, {
    type: 'Population',
    parameter: 0,
    current: 110,
    frames: [1, 5],
    values: [90, 100],
  })
  const second = trace.tool('get_statistics', 20, {
    type: 'Population',
    parameter: 0,
    current: 140,
    frames: [5, 10, 15, 99],
    values: [101, 111, 120, 999],
  })
  const { session, end } = await trace.open()
  assert.deepEqual(session.city(trace.start).metrics, [])
  assert.equal(session.city(first).points.length, 3)
  assert.equal(session.city(first).selected?.latest?.value, 110)
  const full = session.city(end)
  assert.deepEqual(
    full.points.map((point) => [point.frame, point.value]),
    [
      [1, 90],
      [5, 101],
      [10, 111],
      [15, 120],
      [20, 140],
    ],
  )
  assert.equal(full.points[0].gameTime, undefined)
  assert.equal(full.points[2].index, second)
  assert.equal(session.city(end, { frame: 6 }).selected?.latest?.value, 101)
  assert.equal(session.city(end, { frame: 0 }).selected?.latest, undefined)
  assert.equal(session.city(first, { frame: 100 }).cursor?.frame, 10)
  assert.equal(session.city(trace.start, { frame: 100 }).cursor, undefined)
})

test('finance, services, demand and notifications preserve units, scope, zero values and negative margins', async () => {
  const trace = fixture()
  trace.tool('get_budget', 10, {
    totalIncome: 100,
    totalExpenses: 150,
    balance: -50,
    income: { Residential: 80 },
    expenses: { Roads: 20 },
  })
  trace.tool('get_services', 12, {
    electricity: { production: 10, import: 2, consumption: 20 },
    water: { freshCapacity: 100, freshConsumption: 50, sewageCapacity: 0, sewageConsumption: 20 },
    garbage: { productionRate: 30 },
  })
  trace.tool('get_demand', 15, {
    residential: { buildingDemand: { lowDensity: 0 }, factors: { lowDensity: { Taxes: -5 } } },
    industrial: { companyDemand: 999 },
  })
  trace.tool('list_notifications', 20, {
    total: 100,
    returned: 1,
    truncated: true,
    countsByType: { Traffic: 70 },
    notifications: [{ type: 'Traffic' }],
  })
  const { session, end } = await trace.open()
  const metrics = session.city(end).metrics
  const metric = (key: string) => metrics.find((metric) => metric.key === key)!
  assert.equal(metric('budget.balance').latest?.value, -50)
  assert.equal(metric('budget.balance').unit, 'currency / month')
  assert.equal(metric('services.powerMargin').latest?.value, -8)
  assert.equal(metric('services.waterMargin').latest?.value, 50)
  assert.equal(metric('services.sewageMargin').latest?.value, -20)
  assert.equal(metric('services.garbage.productionRate').unit, 'game units / day')
  assert.equal(metric('demand.residential.buildingDemand.lowDensity').latest?.value, 0)
  assert.equal(metric('demand.residential.factors.lowDensity.Taxes').latest?.value, -5)
  assert.equal(metric('demand.industrial.companyDemand').latest?.value, 999)
  assert.equal(metric('notifications.Traffic').latest?.value, 70)
  assert.match(metric('notifications.Traffic').scope!, /Citywide/)
  assert.equal(metric('notifications.total').latest?.frame, 20)
})

test('road histories compare entity and version, never average a changing or truncated list', async () => {
  const trace = fixture()
  const road = (index: number, version: number, flowPercent: number) => ({
    entity: { index, version },
    prefab: 'Road',
    traffic: { flowPercent },
  })
  trace.tool('list_networks', 10, { kind: 'road', truncated: true, networks: [road(1, 1, 20), road(2, 1, 40)] })
  trace.tool('list_networks', 20, { kind: 'road', networks: [road(1, 1, 60), road(1, 2, 80)] })
  const { session, end } = await trace.open()
  const view = session.city(end, { metric: 'road.1:1.flowPercent' })
  assert.deepEqual(
    view.points.map((point) => point.value),
    [20, 60],
  )
  assert.equal(view.metrics.length, 3)
  assert.equal(view.metrics.find((metric) => metric.key === 'road.2:1.flowPercent')?.latest?.frame, 10)
  assert.match(view.selected!.scope!, /never treated as a citywide/)
})

test('an exhaustive notification count snapshot observes zero for resolved types and preserves filtered detail scope', async () => {
  const trace = fixture()
  trace.tool('list_notifications', 10, { total: 4, countsByType: { Traffic: 4 }, notifications: [] })
  trace.tool('list_notifications', 20, {
    total: 2,
    matched: 2,
    truncated: true,
    countsByType: { Garbage: 2 },
    notifications: [
      { type: 'Garbage', priority: 100, target: { index: 1, version: 2, prefab: 'House' }, location: { x: 10, z: 20 } },
    ],
  })
  const { session, end } = await trace.open()
  const view = session.city(end, { metric: 'notifications.Traffic' })
  assert.deepEqual(
    view.points.map((point) => point.value),
    [4, 0],
  )
  assert.equal(view.notifications?.items.length, 1)
  assert.equal(view.notifications?.total, 2)
  assert.equal(view.notifications?.truncated, true)
  assert.equal(view.notifications?.items[0].target, 'House (1:2)')
  assert.equal(view.notifications?.items[0].location, '10, 20')
})

test('write ledgers and events retain failed mutations, exact before/after frames and evidence navigation', async () => {
  const trace = fixture()
  const write = trace.tool(
    'place_building',
    10,
    {},
    {
      success: false,
      status: 'error',
      evidence: {
        before: { frame: 10, paused: true, money: 100, buildings: 0 },
        after: { frame: 10, paused: true, money: 90, buildings: 1 },
      },
    },
  )
  trace.tool('set_budget', 20, {}, { arguments: JSON.stringify({ kind: 'tax', name: 'Residential', value: 5 }) })
  trace.tool(
    'set_simulation',
    20,
    { hours: 2, completed: true },
    {
      arguments: JSON.stringify({ action: 'advance', hours: 2 }),
      timeline: { applied: trace.stamp(20), completed: trace.stamp(40) },
    },
  )
  const { session, end } = await trace.open()
  const view = session.city(end, { metric: 'treasury' })
  assert.equal(view.cursor?.frame, 40)
  assert.equal(view.selected?.latest?.value, 90)
  const event = view.events.find((event) => event.index === write)!
  assert.equal(event.verdict, 'diverged')
  assert.deepEqual(
    event.changes.map((change) => change.delta),
    [-10, 1],
  )
  assert.equal(event.status, 'error')
  assert.match(view.events[0].label, /Advance 2 game hours/)
  assert.match(view.events[1].label, /Residential → 5/)
  assert.equal(session.city(end, { frame: 10 }).events.length, 1)
  const evidence = await session.evidence({ kind: 'record', index: event.index }, write)
  assert.equal(evidence.sections[0].title, 'city.tool.result')
})

test('missing anchors, invalid results, failed reads and malformed history cannot become invented observations', async () => {
  const trace = fixture()
  trace.tool('get_budget', undefined, { balance: 20 })
  trace.tool('get_budget', 10, { balance: 999 }, { success: false })
  trace.tool('get_services', 20, {}, { result: 'not structured JSON' })
  trace.tool('get_statistics', 30, { type: 'Population', parameter: 0, current: 100, frames: [10, 20], values: [90] })
  const { session, end } = await trace.open()
  const view = session.city(end)
  assert.equal(view.untimed, 1)
  assert.equal(
    view.metrics.some((metric) => metric.key === 'budget.balance'),
    false,
  )
  assert.equal(view.pointTotal, 1)
  assert.equal(view.selected?.latest?.value, 100)
  assert.equal(view.warnings.length, 2)
})

test('city images are selected by acquired cursor and city time and loaded as real attachment blobs', async () => {
  const trace = fixture()
  const png = new Blob([new Uint8Array([137, 80, 78, 71])]),
    ref = { $asset: 'assets/map.png', mediaType: 'image/png', bytes: png.size }
  trace.assets.push({ path: 'city/assets/map.png', file: png })
  const first = trace.tool('map_image', 10, null, { files: [{ name: 'image.png', asset: ref }] })
  const second = trace.tool('screenshot', 20, null, { files: [{ name: 'image.png', asset: ref }] })
  const { session, end } = await trace.open()
  assert.equal(session.city(trace.start).media.length, 0)
  assert.equal(session.city(first).media.length, 1)
  assert.equal(session.city(end, { frame: 10 }).media.length, 1)
  assert.deepEqual(
    session.city(end).media.map((item) => item.index),
    [first, second],
  )
  assert.deepEqual(
    new Uint8Array(await (await session.cityImage(first))!.blob.arrayBuffer()),
    new Uint8Array([137, 80, 78, 71]),
  )
})

test('long city series keep all readings in bounded pages while chart points and action markers remain bounded', async () => {
  const trace = fixture()
  for (let frame = 1; frame <= 1500; frame++) {
    const current = frame === 333 ? 1000000 : frame === 777 ? -1000000 : frame
    trace.tool('get_statistics', frame, { type: 'Population', parameter: 0, current, frames: [], values: [] })
    trace.tool('set_budget', frame, {}, { arguments: JSON.stringify({ kind: 'tax', name: 'Residential', value: 5 }) })
  }
  const { session, end } = await trace.open()
  const view = session.city(end, { offset: 50, eventOffset: 30 })
  assert.equal(view.pointTotal, 1500)
  assert.equal(view.points.length, 50)
  assert.equal(view.points[0].frame, 51)
  assert.equal(view.chart[0].frame, 1)
  assert.equal(view.chart.at(-1)?.frame, 1500)
  assert.ok(view.chart.length <= 600)
  assert.ok(view.chart.some((point) => point.value === 1000000))
  assert.ok(view.chart.some((point) => point.value === -1000000))
  const selected = session.city(end, { frame: 998 })
  assert.equal(selected.selected?.latest?.frame, 998)
  assert.ok(selected.chart.some((point) => point.frame === 998))
  assert.ok(selected.chart.length <= 600)
  const recent = session.city(end, { chartLimit: 50 })
  assert.equal(recent.chart.length, 50)
  assert.equal(recent.chart[0].frame, 1451)
  assert.equal(recent.chart.at(-1)?.frame, 1500)
  assert.equal(recent.pointTotal, 1500)
  assert.equal(view.events.length, 30)
  assert.equal(view.eventTotal, 1500)
  assert.ok(view.markers.length <= 300)
})

test('labor rates preserve the native percentage scale', async () => {
  const trace = fixture()
  trace.tool('get_labor', 10, { unemploymentRate: 7.5, homelessnessRate: 0.25 })
  const { session, end } = await trace.open()
  const view = session.city(end, { metric: 'labor.unemploymentRate' })
  assert.equal(view.selected?.unit, '%')
  assert.equal(view.selected?.latest?.value, 7.5)
  assert.equal(view.metrics.find((metric) => metric.key === 'labor.homelessnessRate')?.latest?.value, 0.25)
})
