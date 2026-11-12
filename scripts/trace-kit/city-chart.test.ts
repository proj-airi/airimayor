import assert from 'node:assert/strict'
import { test } from 'node:test'
import { cityTrendRange, cityTrendTicks, cityRoads, cityValue, defaultCityMetric } from './city-chart'
import type { CityMetric, CityPoint } from './city-view'

const point = (frame: number, value: number): CityPoint => ({ frame, value, index: frame, kind: 'read' })
const metric = (key: string, category: CityMetric['category'], label = key): CityMetric => ({
  key,
  category,
  label,
  unit: 'citizens',
  samples: 1,
})

test('City defaults prioritize population and monthly balance regardless of capture order, retaining an explicit choice', () => {
  const metrics = [
    metric('labor.employed', 'Population'),
    metric('population', 'Population'),
    metric('budget.totalIncome', 'Finance'),
    metric('treasury', 'Finance'),
    metric('budget.balance', 'Finance'),
  ]
  assert.equal(defaultCityMetric(metrics, 'Population')?.key, 'population')
  assert.equal(defaultCityMetric(metrics, 'Finance')?.key, 'budget.balance')
  assert.equal(defaultCityMetric(metrics, 'Finance', 'treasury')?.key, 'treasury')
  assert.equal(defaultCityMetric([], 'Traffic'), undefined)
})

test('trend labels use exact dated anchors and keep cross-year dates unambiguous', () => {
  const ticks = cityTrendTicks(
    [point(100, -500), point(200, 100), point(300, 500)],
    [
      { frame: 0, gameTime: '2020-01-01 00:00' },
      { frame: 100, gameTime: '2026-12-31 23:00' },
      { frame: 300, gameTime: '2027-01-01 01:00' },
      { frame: 10000, gameTime: '2030-01-01 00:00' },
    ],
    900,
  )
  assert.deepEqual(
    ticks.map((tick) => tick.label),
    ['2026-12-31 23:00', '2027-01-01 01:00', '2030-01-01 00:00'],
  )
  assert.deepEqual(cityTrendRange([point(100, 10), point(300, 20)], [{ frame: 10000 }]), { start: 100, end: 10000 })
  const flat = cityTrendTicks([point(100, 0)], [], 330)
  assert.deepEqual(flat, [{ frame: 100, label: 'Latest sample' }])
})

test('road choices collapse measures while retaining entity versions and rate/currency units', () => {
  assert.deepEqual(
    cityRoads([
      metric('road.72:1.flowPercent', 'Traffic', 'Medium Road 72:1: flowPercent'),
      metric('road.72:1.volumeIndex', 'Traffic', 'Medium Road 72:1: volumeIndex'),
      metric('road.72:2.flowPercent', 'Traffic', 'Medium Road 72:2: flowPercent'),
    ]),
    [
      { key: '72:1', label: 'Medium Road 72:1' },
      { key: '72:2', label: 'Medium Road 72:2' },
    ],
  )
  assert.equal(cityValue(7.5, '%'), '7.5%')
  assert.equal(cityValue(-1500, 'currency / month'), '₡-1,500 /mo')
  assert.equal(cityValue(undefined, 'currency'), '—')
})
