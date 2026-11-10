import type { Recording } from './viewer-model'
import { sampleCityPoints } from './city-chart'
import { isAsset, isObject, payload } from './viewer-model'
import { judgeWrite, LEDGER_FIELDS, type Verdict } from './write-evidence'

export type CityCategory =
  | 'Population'
  | 'Finance'
  | 'Demand'
  | 'Services'
  | 'Traffic'
  | 'Notifications'
  | 'Construction'
  | 'Statistics'
export interface CityStamp {
  frame: number
  gameTime?: string
}
export interface CityPoint extends CityStamp {
  value: number
  index: number
  kind: 'read' | 'history' | 'before' | 'after'
}
export interface CityMetric {
  key: string
  label: string
  category: CityCategory
  unit: string
  scope?: string
  latest?: CityPoint
  previous?: CityPoint
  samples: number
}
export interface CityEvent {
  index: number
  tool: string
  label: string
  status: string
  frame?: number
  gameTime?: string
  verdict?: Verdict
  note?: string
  changes: { field: string; before: number; after: number; delta: number }[]
}
export interface CityMedia {
  index: number
  tool: string
  frame?: number
  gameTime?: string
}
export interface CityQuery {
  metric?: string
  frame?: number
  offset?: number
  eventOffset?: number
  chartLimit?: number
}
export interface CityNotifications extends CityStamp {
  index: number
  total?: number
  matched?: number
  truncated: boolean
  items: { type: string; priority?: number; target: string; location: string }[]
}
export interface CityView {
  cursor?: CityStamp
  frames: CityStamp[]
  metrics: CityMetric[]
  selected?: CityMetric
  points: CityPoint[]
  pointTotal: number
  chart: CityPoint[]
  events: CityEvent[]
  eventTotal: number
  media: CityMedia[]
  warnings: string[]
  observations: number
  untimed: number
  markers: Pick<CityEvent, 'index' | 'label' | 'status' | 'frame'>[]
  notifications?: CityNotifications
}

interface Series extends Omit<CityMetric, 'latest' | 'previous' | 'samples'> {
  points: CityPoint[]
}
interface Observation {
  index: number
  stamp?: CityStamp
}
const number = (value: unknown): number | undefined =>
  typeof value === 'number' && Number.isFinite(value) ? value : undefined
const stampOf = (value: unknown): CityStamp | undefined =>
  isObject(value) && number(value.frame) !== undefined
    ? { frame: value.frame as number, gameTime: typeof value.gameTime === 'string' ? value.gameTime : undefined }
    : undefined
const at = (value: unknown, path: string): unknown =>
  path.split('.').reduce<unknown>((item, key) => (isObject(item) ? item[key] : undefined), value)
const reads = new Set([
  'get_statistics',
  'get_budget',
  'get_services',
  'get_demand',
  'get_labor',
  'list_notifications',
  'list_networks',
])
const writeLabels: Record<string, string> = {
  place_building: 'Place building',
  build_network: 'Build connection',
  replace_road_type: 'Replace road',
  set_road_features: 'Change road features',
  demolish: 'Demolish',
  zone: 'Zone land',
  set_facility_upgrade: 'Change facility upgrade',
  add_transit_line: 'Add transit line',
  remove_transit_line: 'Remove transit line',
  expand_operational_area: 'Expand operational area',
  buy_tiles: 'Buy land',
  purchase_development_node: 'Unlock development',
  set_budget: 'Adjust finances',
  set_policy: 'Change policy',
  set_simulation: 'Change city clock',
}
const ledgerLabels: Record<string, string> = {
  money: 'Treasury',
  developmentPoints: 'Development points',
  buildings: 'Buildings',
  networkEdges: 'Network edges',
  transitLines: 'Transit lines',
}

export class CityRecording {
  private series = new Map<string, Series>()
  private observations: Observation[] = []
  private anchors: { index: number; stamp: CityStamp }[] = []
  private events: CityEvent[] = []
  private media: CityMedia[] = []
  private warnings: { index: number; message: string }[] = []
  private notificationTypes = new Set<string>()
  private notifications: CityNotifications[] = []

  constructor(recording: Recording) {
    recording.records.forEach((record, index) => {
      if (record.type !== 'instant' || record.name !== 'city.tool.result') return
      const data = payload(recording, record)
      if (typeof data.tool !== 'string') return
      const tool = data.tool
      const timeline = isObject(data.timeline) ? data.timeline : {}
      const submitted = stampOf(timeline.submitted),
        applied = stampOf(timeline.applied),
        completed = stampOf(timeline.completed)
      const stamp = tool === 'set_simulation' ? (completed ?? applied) : (applied ?? completed)
      for (const value of [submitted, applied, completed]) if (value) this.anchors.push({ index, stamp: value })
      const parse = (value: unknown, label: string): Record<string, unknown> => {
        if (typeof value !== 'string') return {}
        try {
          const parsed: unknown = JSON.parse(value)
          return isObject(parsed) ? parsed : {}
        } catch {
          this.warnings.push({ index, message: `${tool}: invalid ${label}; structured values are unavailable.` })
          return {}
        }
      }
      const result = reads.has(tool) || tool === 'set_simulation' ? parse(data.result, 'result') : {}
      if (data.success === true && reads.has(tool)) {
        this.observations.push({ index, stamp })
        if (stamp) this.read(tool, result, index, stamp)
      }
      if (isObject(data.evidence)) {
        for (const kind of ['before', 'after'] as const) {
          const ledger = data.evidence[kind]
          const ledgerStamp = stampOf(ledger)
          if (!ledgerStamp || !isObject(ledger)) continue
          // A ledger has its own frame; a call's date only labels that exact frame.
          ledgerStamp.gameTime = [applied, completed, submitted].find(
            (stamp) => stamp?.frame === ledgerStamp.frame,
          )?.gameTime
          this.anchors.push({ index, stamp: ledgerStamp })
          for (const field of LEDGER_FIELDS)
            this.add(
              field === 'money' ? 'treasury' : `ledger.${field}`,
              ledgerLabels[field],
              field === 'money' ? 'Finance' : 'Construction',
              field === 'money' ? 'currency' : 'count',
              ledger[field],
              index,
              ledgerStamp,
              kind,
            )
        }
      }
      if (Object.hasOwn(writeLabels, tool)) {
        const args = parse(data.arguments, 'arguments')
        let label = writeLabels[tool]
        if (tool === 'set_budget')
          label = `Adjust ${args.kind ?? 'finances'}${args.name ? `: ${args.name}` : ''}${args.value !== undefined ? ` → ${args.value}` : ''}`
        else if (tool === 'set_simulation' && args.action === 'advance')
          label = `Advance ${result.hours ?? args.hours ?? 1} game hours`
        else if (typeof args.prefab === 'string') label += `: ${args.prefab}`
        const evidence = isObject(data.evidence) ? data.evidence : {}
        const before = isObject(evidence.before) ? evidence.before : {},
          after = isObject(evidence.after) ? evidence.after : {}
        const changes = LEDGER_FIELDS.flatMap((field) => {
          const a = number(before[field]),
            b = number(after[field])
          return a !== undefined && b !== undefined && a !== b
            ? [{ field: ledgerLabels[field], before: a, after: b, delta: b - a }]
            : []
        })
        const judgment = judgeWrite(tool, data)
        this.events.push({
          index,
          tool,
          label,
          status: typeof data.status === 'string' ? data.status : data.success ? 'ok' : 'error',
          ...stamp,
          verdict: judgment.verdict,
          note:
            tool === 'set_simulation'
              ? [
                  result.completed === false || result.targetReached === false
                    ? 'The requested time advance did not complete.'
                    : '',
                  typeof result.note === 'string' ? result.note : '',
                ]
                  .filter(Boolean)
                  .join(' ') || undefined
              : judgment.message || undefined,
          changes,
        })
      }
      if (
        data.success === true &&
        (tool === 'map_image' || tool === 'screenshot') &&
        Array.isArray(data.files) &&
        data.files.some((file) => isObject(file) && isAsset(file.asset) && file.asset.mediaType.startsWith('image/'))
      )
        this.media.push({ index, tool, ...stamp })
    })
  }

  private add(
    key: string,
    label: string,
    category: CityCategory,
    unit: string,
    value: unknown,
    index: number,
    stamp: CityStamp,
    kind: CityPoint['kind'] = 'read',
    scope?: string,
  ) {
    const numeric = number(value)
    if (numeric === undefined) return
    let series = this.series.get(key)
    if (!series) {
      series = { key, label, category, unit, scope, points: [] }
      this.series.set(key, series)
    }
    series.points.push({ ...stamp, index, value: numeric, kind })
  }

  private read(tool: string, data: Record<string, unknown>, index: number, stamp: CityStamp) {
    const add = (key: string, label: string, category: CityCategory, unit: string, value: unknown, scope?: string) =>
      this.add(key, label, category, unit, value, index, stamp, 'read', scope)
    if (tool === 'get_statistics' && typeof data.type === 'string') {
      const parameter = number(data.parameter) ?? 0
      const key =
        parameter === 0 && data.type === 'Population'
          ? 'population'
          : parameter === 0 && data.type === 'Money'
            ? 'treasury'
            : `stat.${data.type}.${parameter}`
      const label = key === 'treasury' ? 'Treasury' : data.type + (parameter ? ` [${parameter}]` : '')
      const category = key === 'population' ? 'Population' : key === 'treasury' ? 'Finance' : 'Statistics'
      const unit = key === 'population' ? 'citizens' : key === 'treasury' ? 'currency' : 'native units'
      if (Array.isArray(data.frames) && Array.isArray(data.values) && data.frames.length === data.values.length) {
        data.frames.forEach((frame, sample) => {
          if (number(frame) !== undefined && frame <= stamp.frame)
            this.add(
              key,
              label,
              category,
              unit,
              (data.values as unknown[])[sample],
              index,
              { frame, gameTime: frame === stamp.frame ? stamp.gameTime : undefined },
              'history',
            )
        })
      } else
        this.warnings.push({
          index,
          message: `${data.type}: history frames and values do not match; only the current reading is shown.`,
        })
      this.add(key, label, category, unit, data.current, index, stamp)
    }
    if (tool === 'get_labor') {
      for (const [path, label, unit] of [
        ['employed', 'Employed citizens', 'citizens'],
        ['unemploymentRate', 'Unemployment rate', '%'],
        ['homelessCitizens', 'Homeless citizens', 'citizens'],
        ['homelessnessRate', 'Homelessness rate', '%'],
        ['jobs.total', 'Jobs', 'jobs'],
        ['jobs.free', 'Vacant jobs', 'jobs'],
        ['ageStructure.totalCitizens', 'Moved-in citizens', 'citizens'],
        ['ageStructure.children', 'Children', 'citizens'],
        ['ageStructure.teens', 'Teens', 'citizens'],
        ['ageStructure.adults', 'Adults', 'citizens'],
        ['ageStructure.seniors', 'Seniors', 'citizens'],
      ])
        add(`labor.${path}`, label, 'Population', unit, at(data, path))
      for (const group of ['totalByEducation', 'freeByEducation']) {
        const values = at(data, `jobs.${group}`)
        if (isObject(values))
          for (const [name, value] of Object.entries(values))
            add(
              `labor.${group}.${name}`,
              `${group === 'totalByEducation' ? 'Jobs' : 'Vacant jobs'}: ${name}`,
              'Population',
              'jobs',
              value,
            )
      }
    }
    if (tool === 'get_budget') {
      for (const [field, label] of [
        ['totalIncome', 'Monthly income'],
        ['totalExpenses', 'Monthly expenses'],
        ['totalTaxIncome', 'Monthly tax income'],
        ['balance', 'Monthly balance'],
        ['moneyDelta', 'Money delta'],
      ])
        add(
          `budget.${field}`,
          label,
          'Finance',
          field === 'moneyDelta' ? 'native units' : 'currency / month',
          data[field],
        )
      for (const group of ['income', 'expenses'])
        if (isObject(data[group]))
          for (const [name, value] of Object.entries(data[group]))
            add(
              `budget.${group}.${name}`,
              `${group === 'income' ? 'Income' : 'Expense'}: ${name}`,
              'Finance',
              'currency / month',
              value,
            )
    }
    if (tool === 'get_demand') {
      for (const [path, label] of [
        ['residential.buildingDemand.lowDensity', 'Low-density housing'],
        ['residential.buildingDemand.mediumDensity', 'Medium-density housing'],
        ['residential.buildingDemand.highDensity', 'High-density housing'],
        ['commercial.buildingDemand', 'Commercial'],
        ['industrial.buildingDemand', 'Industrial'],
        ['office.buildingDemand', 'Office'],
        ['storage.buildingDemand', 'Storage'],
      ])
        add(`demand.${path}`, label, 'Demand', '0–255 demand', at(data, path))
      for (const [path, label] of [
        ['residential.householdDemand', 'Household demand'],
        ['commercial.companyDemand', 'Commercial company demand'],
        ['industrial.companyDemand', 'Industrial company demand'],
        ['office.companyDemand', 'Office company demand'],
        ['storage.companyDemand', 'Storage company demand'],
      ])
        add(`demand.${path}`, label, 'Demand', 'native demand', at(data, path))
      for (const path of [
        'residential.factors.lowDensity',
        'residential.factors.mediumDensity',
        'residential.factors.highDensity',
        'commercial.factors',
        'industrial.factors',
        'office.factors',
      ]) {
        const factors = at(data, path)
        if (isObject(factors))
          for (const [name, value] of Object.entries(factors))
            add(
              `demand.${path}.${name}`,
              `${path.replace('.factors', '')}: ${name}`,
              'Demand',
              'signed contribution',
              value,
            )
      }
    }
    if (tool === 'get_services') {
      for (const group of ['electricity', 'water', 'garbage'])
        if (isObject(data[group]))
          for (const [name, value] of Object.entries(data[group]))
            add(
              `services.${group}.${name}`,
              `${group}: ${name}`,
              'Services',
              name === 'productionRate' ? 'game units / day' : 'game units',
              value,
            )
      for (const [key, label, paths] of [
        [
          'powerMargin',
          'Power production + imports − consumption',
          ['electricity.production', 'electricity.import', 'electricity.consumption'],
        ],
        ['waterMargin', 'Fresh water capacity − consumption', ['water.freshCapacity', 'water.freshConsumption']],
        ['sewageMargin', 'Sewage capacity − consumption', ['water.sewageCapacity', 'water.sewageConsumption']],
      ] as const) {
        const values = paths.map((path) => number(at(data, path)))
        if (values.every((value): value is number => value !== undefined))
          add(
            `services.${key}`,
            label,
            'Services',
            'game units',
            values.slice(0, -1).reduce((sum, value) => sum + value, 0) - values.at(-1)!,
          )
      }
    }
    if (tool === 'list_notifications') {
      add('notifications.total', 'City notifications', 'Notifications', 'notifications', data.total)
      if (isObject(data.countsByType)) {
        Object.keys(data.countsByType).forEach((name) => this.notificationTypes.add(name))
        for (const name of this.notificationTypes) {
          const value = Object.hasOwn(data.countsByType, name) ? data.countsByType[name] : 0
          add(
            `notifications.${name}`,
            name,
            'Notifications',
            'notifications',
            value,
            'Citywide counts, including when detail rows are filtered or truncated.',
          )
        }
      }
      this.notifications.push({
        ...stamp,
        index,
        total: number(data.total),
        matched: number(data.matched),
        truncated: data.truncated === true,
        items: Array.isArray(data.notifications)
          ? data.notifications.slice(0, 64).flatMap((item) => {
              if (!isObject(item) || typeof item.type !== 'string') return []
              const target = isObject(item.target) ? item.target : {},
                location = isObject(item.location) ? item.location : {}
              return [
                {
                  type: item.type,
                  priority: number(item.priority),
                  target:
                    typeof target.prefab === 'string' ? `${target.prefab} (${target.index}:${target.version})` : '',
                  location:
                    number(location.x) !== undefined && number(location.z) !== undefined
                      ? `${location.x}, ${location.z}`
                      : '',
                },
              ]
            })
          : [],
      })
    }
    if (tool === 'list_networks' && data.kind === 'road' && Array.isArray(data.networks)) {
      for (const road of data.networks) {
        if (
          !isObject(road) ||
          !isObject(road.entity) ||
          number(road.entity.index) === undefined ||
          number(road.entity.version) === undefined ||
          !isObject(road.traffic)
        )
          continue
        const entity = `${road.entity.index}:${road.entity.version}`
        for (const [field, unit] of [
          ['flowPercent', '% flow'],
          ['volumeIndex', 'volume index'],
          ['congestionIndex', 'congestion index'],
          ['activeBottlenecks', 'bottlenecks'],
        ])
          add(
            `road.${entity}.${field}`,
            `${road.prefab ?? 'Road'} ${entity}: ${field}`,
            'Traffic',
            unit,
            road.traffic[field],
            'One recorded road entity; limited network lists are never treated as a citywide traffic average.',
          )
      }
    }
  }

  view(cursor: number, query: CityQuery = {}): CityView {
    const anchors = this.anchors.filter((anchor) => anchor.index <= cursor)
    const latestAnchor = anchors.at(-1)?.stamp
    const frame = latestAnchor ? Math.min(query.frame ?? latestAnchor.frame, latestAnchor.frame) : undefined
    const frames = new Map<number, CityStamp>()
    for (const anchor of anchors)
      if (!frames.get(anchor.stamp.frame)?.gameTime) frames.set(anchor.stamp.frame, anchor.stamp)
    const series = [...this.series.values()]
      .map((series) => {
        const byFrame = new Map<number, CityPoint>()
        for (const point of series.points)
          if (point.index <= cursor) {
            byFrame.set(point.frame, { ...point, gameTime: point.gameTime ?? frames.get(point.frame)?.gameTime })
            if (!frames.has(point.frame)) frames.set(point.frame, { frame: point.frame, gameTime: point.gameTime })
          }
        return { ...series, points: [...byFrame.values()].sort((a, b) => a.frame - b.frame) }
      })
      .filter((series) => series.points.length)
    const metric = (series: Series): CityMetric => {
      const points = series.points.filter((point) => frame === undefined || point.frame <= frame)
      const { points: _points, ...definition } = series
      return { ...definition, latest: points.at(-1), previous: points.at(-2), samples: series.points.length }
    }
    const metrics = series.map(metric)
    const selected =
      series.find((series) => series.key === query.metric) ??
      series.find((series) => series.key === 'population') ??
      series[0]
    const points = selected?.points ?? []
    const selectedMetric = metrics.find((metric) => metric.key === selected?.key)
    const chartPoints = query.chartLimit ? points.slice(-query.chartLimit) : points
    const chart = sampleCityPoints(chartPoints, 599)
    const current = selectedMetric?.latest
    if (
      current &&
      current.frame >= (chartPoints[0]?.frame ?? 0) &&
      !chart.some((point) => point.frame === current.frame)
    ) {
      chart.push(current)
      chart.sort((a, b) => a.frame - b.frame)
    }
    const events = this.events
      .filter(
        (event) => event.index <= cursor && (frame === undefined || event.frame === undefined || event.frame <= frame),
      )
      .reverse()
    const media = ['map_image', 'screenshot'].flatMap((tool) =>
      this.media
        .filter(
          (item) =>
            item.tool === tool &&
            item.index <= cursor &&
            (frame === undefined || (item.frame !== undefined && item.frame <= frame)),
        )
        .slice(-1),
    )
    return {
      cursor: frame === undefined ? undefined : (frames.get(frame) ?? { frame }),
      frames: [...frames.values()].sort((a, b) => a.frame - b.frame),
      metrics,
      selected: selectedMetric,
      points: points.slice(query.offset ?? 0, (query.offset ?? 0) + 50),
      pointTotal: points.length,
      chart,
      events: events.slice(query.eventOffset ?? 0, (query.eventOffset ?? 0) + 30),
      eventTotal: events.length,
      markers: events
        .filter((event) => event.frame !== undefined)
        .filter((_event, index, all) => all.length <= 300 || index % Math.ceil(all.length / 300) === 0)
        .map(({ index, label, status, frame }) => ({ index, label, status, frame })),
      media,
      warnings: this.warnings.filter((warning) => warning.index <= cursor).map((warning) => warning.message),
      observations: this.observations.filter((observation) => observation.index <= cursor).length,
      untimed: this.observations.filter((observation) => observation.index <= cursor && !observation.stamp).length,
      notifications: this.notifications
        .filter((item) => item.index <= cursor && (frame === undefined || item.frame <= frame))
        .at(-1),
    }
  }
}
