import type { CityCategory, CityMetric, CityPoint, CityStamp } from './city-view'

export function defaultCityMetric(metrics: CityMetric[], category: CityCategory, requested = '') {
  const available = metrics.filter((metric) => metric.category === category)
  const defaults: Partial<Record<CityCategory, string[]>> = {
    Population: ['population', 'labor.employed'],
    Finance: ['budget.balance', 'treasury', 'budget.totalIncome'],
    Demand: ['demand.residential.buildingDemand.lowDensity'],
    Services: ['services.powerMargin', 'services.waterMargin'],
    Notifications: ['notifications.total'],
    Construction: ['ledger.buildings', 'ledger.networkEdges', 'ledger.transitLines'],
  }
  return (
    available.find((metric) => metric.key === requested) ??
    defaults[category]?.flatMap((key) => available.filter((metric) => metric.key === key))[0] ??
    available[0]
  )
}

const serviceLabels: Record<string, string> = {
  production: 'Power production',
  consumption: 'Power consumption',
  fulfilledConsumption: 'Power delivered',
  import: 'Power imports',
  export: 'Power exports',
  batteryCharge: 'Battery charge',
  batteryCapacity: 'Battery capacity',
  freshCapacity: 'Fresh water capacity',
  freshConsumption: 'Fresh water use',
  sewageCapacity: 'Sewage capacity',
  sewageConsumption: 'Sewage produced',
  freshImport: 'Water imports',
  freshExport: 'Water exports',
  sewageExport: 'Sewage exports',
  productionRate: 'Daily garbage generated',
}
export function cityMetricLabel(metric: CityMetric) {
  if (metric.key.startsWith('services.'))
    return (
      serviceLabels[metric.key.split('.').at(-1)!] ??
      {
        'services.powerMargin': 'Power surplus',
        'services.waterMargin': 'Fresh water surplus',
        'services.sewageMargin': 'Sewage capacity remaining',
      }[metric.key] ??
      metric.label
    )
  if (metric.key.startsWith('road.'))
    return trafficMeasures.find((item) => metric.key.endsWith(`.${item.key}`))?.label ?? metric.label
  return metric.label
    .replace('residential.lowDensity:', 'Low-density housing ·')
    .replace('residential.mediumDensity:', 'Medium-density housing ·')
    .replace('residential.highDensity:', 'High-density housing ·')
    .replace('.factors', '')
    .replace(/([a-z])([A-Z])/g, '$1 $2')
}

export const trafficMeasures = [
  { key: 'flowPercent', label: 'Flow' },
  { key: 'volumeIndex', label: 'Traffic volume' },
  { key: 'congestionIndex', label: 'Congestion' },
  { key: 'activeBottlenecks', label: 'Bottlenecks' },
]
export function cityRoads(metrics: CityMetric[]) {
  return [
    ...new Map(
      metrics
        .filter((metric) => metric.category === 'Traffic')
        .map((metric) => {
          const road = metric.key.split('.')[1]
          return [road, { key: road, label: metric.label.replace(/:\s*[^:]+$/, '') }]
        }),
    ).values(),
  ]
}

export function cityNumber(value: number | undefined, compact = false) {
  if (value === undefined) return '—'
  return new Intl.NumberFormat('en-US', {
    maximumFractionDigits: compact ? 1 : 2,
    notation: compact ? 'compact' : 'standard',
  }).format(value)
}
export function cityValue(value: number | undefined, unit: string, compact = false) {
  if (value === undefined) return '—'
  const numeric = cityNumber(value, compact)
  if (unit.startsWith('currency')) return `₡${numeric}${unit.includes('/ month') ? ' /mo' : ''}`
  if (unit === '%' || unit === '% flow') return `${numeric}%`
  return numeric
}
export function cityDate(stamp?: CityStamp) {
  return stamp?.gameTime ?? (stamp ? 'Historical sample' : 'No reading yet')
}

export function sampleCityPoints<T extends CityPoint>(points: T[], limit = 600): T[] {
  if (points.length <= limit) return points
  const result = [points[0]],
    buckets = Math.floor((limit - 2) / 2),
    size = (points.length - 2) / buckets
  for (let bucket = 0; bucket < buckets; bucket++) {
    const from = 1 + Math.floor(bucket * size),
      to = 1 + Math.floor((bucket + 1) * size)
    let low = from,
      high = from
    for (let index = from + 1; index < to; index++) {
      if (points[index].value < points[low].value) low = index
      if (points[index].value > points[high].value) high = index
    }
    for (const index of [...new Set([low, high])].sort((a, b) => a - b)) result.push(points[index])
  }
  result.push(points.at(-1)!)
  return result
}
