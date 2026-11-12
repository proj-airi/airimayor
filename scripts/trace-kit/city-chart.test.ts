import assert from 'node:assert/strict'
import { test } from 'node:test'
import { cityRoads, cityValue, defaultCityMetric } from './city-chart'
import type { CityMetric } from './city-view'

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
