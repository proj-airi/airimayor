import { isDeepStrictEqual } from 'node:util'

export const district = {
  junctions: {
    west: [0, 0], market: [100, 0], east: [200, 0],
    gardens: [100, 80], court: [200, 80], depot: [0, -80],
    pier: [60, -45], ridge: [140, 45],
  },
  roads: {
    avenue: { kind: 'arterial', level: 0, d: 'M west Q 50 -24 market Q 150 24 east' },
    garden: { kind: 'collector', level: 0, d: 'M market L gardens' },
    court: { kind: 'local', level: 0, d: 'M gardens L court L east' },
    depot: { kind: 'local', level: 0, d: 'M west L depot' },
    flyover: { kind: 'arterial', level: 1, d: 'M pier L ridge' },
  },
}

const roadFields = ['kind', 'level', 'd']
const identifier = /^[a-z][a-z0-9_-]*$/
const number = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?$/i

function check(ok, message) {
  if (!ok) throw new Error(message)
}

function object(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function fields(value, allowed) {
  check(object(value) && Object.keys(value).every(key => allowed.includes(key)), 'Unexpected object or field')
}

function validId(id) {
  return identifier.test(id) && !Object.hasOwn(Object.prototype, id)
}

function point(value) {
  check(Array.isArray(value) && value.length === 2 && value.every(Number.isFinite), 'Expected finite x,z')
  return value
}

export function documentFor(representation, source = district) {
  const doc = structuredClone(source)
  if (representation === 'coordinates') {
    for (const road of Object.values(doc.roads)) {
      road.d = road.d.replace(/[a-z][a-z0-9_-]*/g, id => doc.junctions[id].join(' '))
    }
    delete doc.junctions
  }
  return doc
}

export function applyChange(input, change, operation) {
  fields(change, ['remove', 'put'])
  const doc = structuredClone(input)
  const tables = Object.keys(doc)
  const remove = change.remove ?? {}
  const put = change.put ?? {}
  fields(remove, tables)
  fields(put, tables)
  for (const table of tables) {
    const deleted = remove[table] ?? []
    check(Array.isArray(deleted), 'Expected an array of IDs to remove')
    for (const id of deleted) {
      check(typeof id === 'string' && Object.hasOwn(doc[table], id), 'Unknown ID to remove')
      delete doc[table][id]
    }
    const entries = put[table] ?? {}
    check(object(entries), 'Expected a table of changes')
    for (const [id, value] of Object.entries(entries)) {
      check(validId(id), 'Invalid ID')
      const exists = Object.hasOwn(doc[table], id)
      check(operation !== 'rebuild' || !exists, 'Remove the existing row before rebuilding it')
      if (table === 'junctions') {
        doc.junctions[id] = point(value)
      } else {
        fields(value, roadFields)
        doc.roads[id] = { ...(exists ? doc.roads[id] : {}), ...value }
      }
    }
  }
  return doc
}

function parseRoad(road, junctions, used) {
  fields(road, roadFields)
  check(['local', 'collector', 'arterial'].includes(road.kind), 'Unknown street class')
  check([0, 1].includes(road.level), 'Unknown street level')
  check(typeof road.d === 'string', 'Missing street path')
  const tokens = road.d.trim().split(/[\s,]+/)
  let cursor = 0
  function coordinates() {
    const pair = tokens.slice(cursor, cursor + 2)
    check(pair.length === 2 && pair.every(token => number.test(token)), 'Expected a coordinate pair')
    cursor += 2
    return point(pair.map(Number))
  }
  function endpoint() {
    if (!junctions) return { position: coordinates() }
    const id = tokens[cursor++]
    check(Object.hasOwn(junctions, id), 'Unknown junction ' + id)
    used.add(id)
    return { position: junctions[id], id }
  }
  check(tokens[cursor++] === 'M', 'Each street starts with M')
  const vertices = [endpoint()]
  const curves = []
  while (cursor < tokens.length) {
    const command = tokens[cursor++]
    check(['L', 'Q', 'C'].includes(command), 'Expected L, Q or C')
    const controls = []
    if (command !== 'L') controls.push(coordinates())
    if (command === 'C') controls.push(coordinates())
    const next = endpoint()
    curves.push([vertices.at(-1).position, ...controls, next.position])
    vertices.push(next)
  }
  check(curves.length > 0, 'Empty street')
  return { kind: road.kind, level: road.level, curves, vertices }
}

export function network(doc) {
  const used = new Set()
  const roads = {}
  const groups = new Map()
  for (const [id, row] of Object.entries(doc.roads)) {
    const road = parseRoad(row, doc.junctions, used)
    roads[id] = road
    road.vertices.forEach(({ id: ref, position }, index) => {
      const key = JSON.stringify([road.level, ref ?? position])
      if (!groups.has(key)) groups.set(key, [])
      groups.get(key).push(`${id}/${index}`)
    })
  }
  if (doc.junctions) check(used.size === Object.keys(doc.junctions).length, 'Unused junction')
  return { roads, joins: [...groups.values()].filter(group => group.length > 1).map(group => group.sort()).sort() }
}

export function at(curve, t) {
  let points = curve
  while (points.length > 1) {
    points = points.slice(1).map(([x, z], i) => [
      (1 - t) * points[i][0] + t * x, (1 - t) * points[i][1] + t * z,
    ])
  }
  return points[0]
}

export function sameGeometry(a, b) {
  if (!a || !b || a.curves.length !== b.curves.length) return false
  return a.curves.every((curve, i) => [0, 1 / 3, 2 / 3, 1].every(t => {
    const [x, z] = at(curve, t)
    const [bx, bz] = at(b.curves[i], t)
    return Math.hypot(x - bx, z - bz) < 0.01
  }))
}

export function sameRoad(a, b) {
  return sameGeometry(a, b) && a.kind === b.kind && a.level === b.level
}

export function sameJoins(a, b) {
  return isDeepStrictEqual(a.joins, b.joins)
}

export function mapSvg(city, groveRadius) {
  const colors = { arterial: '#9a4824', collector: '#407883', local: '#73716a' }
  const widths = { arterial: 5, collector: 3.5, local: 2.3 }
  const paths = Object.values(city.roads).sort((a, b) => a.level - b.level).map(road => {
    const commands = road.curves.map(curve => [({ 2: 'L', 3: 'Q', 4: 'C' })[curve.length], ...curve.slice(1).flat()])
    const d = ['M', ...road.curves[0][0], ...commands.flat()].join(' ')
    return `<path d="${d}" stroke="${colors[road.kind]}" stroke-width="${widths[road.kind]}"${road.level ? ' stroke-dasharray="6 2"' : ''}/>`
  })
  const grove = groveRadius ? `<circle cx="290" cy="20" r="${groveRadius}" fill="#b3ccaa"/>` : ''
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="-25 -100 435 260" fill="none" stroke-linejoin="round" stroke-linecap="round"><rect x="-25" y="-100" width="435" height="260" fill="#f4f1e9"/>${grove}${paths.join('')}</svg>`
}
