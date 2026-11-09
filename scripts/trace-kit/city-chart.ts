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

export function cityChart(points: CityPoint[], clocks: CityStamp[], width: number, height: number) {
  const left = 60,
    right = Math.max(left + 1, width - 18),
    top = 14,
    bottom = height - 30
  const start = points[0]?.frame ?? 0,
    end = points.at(-1)?.frame ?? start
  const values = points.map((point) => point.value)
  let min = values.length ? Math.min(...values) : 0,
    max = values.length ? Math.max(...values) : 1
  if (min === max) {
    const padding = Math.max(1, Math.abs(min) * 0.05)
    min -= padding
    max += padding
  }
  const raw = (max - min) / 4,
    power = 10 ** Math.floor(Math.log10(raw))
  const step = [1, 2, 5, 10].find((value) => value * power >= raw)! * power
  min = Math.floor(min / step) * step
  max = Math.ceil(max / step) * step
  const x = (frame: number) =>
    end === start ? (left + right) / 2 : left + ((frame - start) / (end - start)) * (right - left)
  const y = (value: number) => bottom - ((value - min) / (max - min)) * (bottom - top)
  const plotted = points.map((point) => ({ ...point, x: x(point.frame), y: y(point.value) }))
  const dated = clocks.filter((clock) => clock.gameTime && clock.frame >= start && clock.frame <= end)
  const showYear = new Set(dated.map((clock) => clock.gameTime!.slice(0, 4))).size > 1
  const desired = width < 550 ? [0, 1] : [0, 0.5, 1]
  const ticks = [
    ...new Map(
      desired.map((fraction) => {
        const frame = start + (end - start) * fraction
        const clock = dated.reduce<CityStamp | undefined>(
          (best, clock) => (!best || Math.abs(clock.frame - frame) < Math.abs(best.frame - frame) ? clock : best),
          undefined,
        )
        const stamp = clock ?? { frame }
        return [
          stamp.frame,
          {
            x: x(stamp.frame),
            frame: stamp.frame,
            label:
              (showYear ? stamp.gameTime : stamp.gameTime?.replace(/^\d{4}-/, '')) ??
              (fraction === 0 ? 'First sample' : 'Latest sample'),
          },
        ]
      }),
    ).values(),
  ]
  return {
    points: plotted,
    polyline: plotted.map((point) => `${point.x},${point.y}`).join(' '),
    left,
    right,
    top,
    bottom,
    start,
    end,
    yTicks: Array.from({ length: Math.round((max - min) / step) + 1 }, (_, index) => ({
      value: min + index * step,
      y: y(min + index * step),
    })),
    xTicks: ticks,
    x,
  }
}

export function sampleCityPoints(points: CityPoint[], limit = 600) {
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
