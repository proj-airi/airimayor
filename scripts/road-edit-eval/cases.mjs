import { at, district, network, sameGeometry, sameJoins, sameRoad } from './streets.mjs'

const original = network(district)

function revision(name, instruction, modify, affected) {
  const target = structuredClone(district)
  modify(target)
  const expected = network(target)
  return {
    name, instruction,
    grade(city) {
      const untouched = Object.keys(original.roads).filter(id => !affected.includes(id))
      return {
        target: affected.every(id => sameRoad(city.roads[id], expected.roads[id])),
        topology: sameJoins(city, expected),
        preservation: Object.keys(city.roads).length === Object.keys(expected.roads).length
          && untouched.every(id => sameRoad(city.roads[id], original.roads[id])),
      }
    },
  }
}

const moveMarket = revision('move-market',
  'Move the ground junction at (100,0) to (116,8), including all its approaches. Keep all curve control points, other junctions, street classes, and the flyover fixed.',
  doc => { doc.junctions.market = [116, 8] }, ['avenue', 'garden'])

const softenBend = revision('reshape-avenue',
  'Reshape only the east half of avenue. Keep its two end junctions and make its quadratic midpoint (150,18). Keep the west half of avenue and all other streets as drawn.',
  doc => { doc.roads.avenue.d = 'M west Q 50 -24 market Q 150 36 east' }, ['avenue'])

const changeClass = revision('change-hierarchy',
  'Promote court to collector and garden to local. Keep every street alignment and connection.',
  doc => { doc.roads.court.kind = 'collector'; doc.roads.garden.kind = 'local' }, ['garden', 'court'])

const relocateBlock = revision('move-block-frontage',
  'Move only the two corners of court at z=80 to z=112, keeping their x coordinates. Turn the section of court between those moved corners into a quadratic with control point (150,144). Keep the street from the eastern corner back to avenue straight. Preserve every existing connection and street class.',
  doc => {
    doc.junctions.gardens = [100, 112]
    doc.junctions.court = [200, 112]
    doc.roads.court.d = 'M gardens Q 150 144 court L east'
  }, ['court', 'garden'])

const newBlock = {
  name: 'design-a-block',
  instruction: 'Extend the district east with exactly three new ground streets. Draw quay, a collector, from the east end of avenue through (260,0) to (340,0). Bow the first section north, with its midpoint between z=-24 and z=-8. Make the second section straight. Close a block south of quay with two local streets: lane runs from (260,0) to a corner of your choice inside x=275..325, z=55..90; mews returns from that corner to (340,0). Use one segment for each local street and keep them inside that rectangle of developable land bounded by x=260..340 and z=0..90. Keep all existing streets.',
  grade(city) {
    const { quay, lane, mews } = city.roads
    const preserved = Object.keys(original.roads).every(id => sameRoad(original.roads[id], city.roads[id]))
      && Object.keys(city.roads).every(id => Object.hasOwn(original.roads, id) || ['quay', 'lane', 'mews'].includes(id))
    const shape = quay?.curves.length === 2 && lane?.curves.length === 1 && mews?.curves.length === 1
    if (!shape) return { target: false, topology: false, preservation: preserved }
    const corner = lane.vertices[1].position
    const inside = ([x, z]) => x >= 260 && x <= 340 && z >= 0 && z <= 90
    const localFits = [lane, mews].every(road =>
      road.kind === 'local' && road.level === 0 && insideCurve(road.curves[0], inside))
    const bow = at(quay.curves[0], 0.5)[1]
    const groundCollector = quay.kind === 'collector' && quay.level === 0
    const expected = structuredClone(district)
    expected.junctions.mid = [260, 0]
    expected.junctions.tip = [340, 0]
    expected.junctions.corner = corner
    expected.roads.quay = { kind: 'collector', level: 0, d: 'M east Q 230 -32 mid L tip' }
    expected.roads.lane = { kind: 'local', level: 0, d: 'M mid L corner' }
    expected.roads.mews = { kind: 'local', level: 0, d: 'M corner L tip' }
    const reference = network(expected)
    return {
      target: groundCollector && localFits && bow >= -24 && bow <= -8
        && corner[0] >= 275 && corner[0] <= 325 && corner[1] >= 55 && corner[1] <= 90
        && quay.vertices[0].position.every((v, i) => v === [200, 0][i])
        && sameGeometry({ curves: [quay.curves[1]] }, { curves: [reference.roads.quay.curves[1]] })
        && lane.vertices[0].position.every((v, i) => v === [260, 0][i])
        && mews.vertices[1].position.every((v, i) => v === [340, 0][i]),
      topology: sameJoins(city, reference),
      preservation: preserved,
    }
  },
}

function insideCurve(curve, inside) {
  return Array.from({ length: 33 }, (_, i) => at(curve, i / 32)).every(inside)
}

function samples(road) {
  return road.curves.flatMap(curve => Array.from({ length: 33 }, (_, i) => at(curve, i / 32)))
}

function encloses(points, [x, z]) {
  let inside = false
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [ax, az] = points[i]
    const [bx, bz] = points[j]
    if ((az > z) !== (bz > z) && x < ax + (bx - ax) * (z - az) / (bz - az)) inside = !inside
  }
  return inside
}

const grove = {
  name: 'design-and-revise-grove',
  instruction: 'Design a block around a protected grove centred at (290,20), within x=200..390 and z=-75..115. Keep all new road centrelines at least 30 m from the grove centre. Continue avenue east from (200,0) with a ground collector called parkway. Give parkway two sections: a curved approach (its midpoint at least 4 m off its chord), then a northern frontage between two new junctions of your choice, one west and one east of the grove. Use two ground local streets to close the block around the grove: orchard from the western frontage junction to a shared southern corner, willow from that corner to the eastern frontage junction. Use one section for each local street. Choose the alignments and proportions yourself. Keep all existing roads.',
  grade(city) {
    const { parkway, orchard, willow } = city.roads
    const preservation = Object.keys(original.roads).every(id => sameRoad(original.roads[id], city.roads[id]))
      && Object.keys(city.roads).every(id => Object.hasOwn(original.roads, id) || ['parkway', 'orchard', 'willow'].includes(id))
    if (parkway?.curves.length !== 2 || orchard?.curves.length !== 1 || willow?.curves.length !== 1)
      return { target: false, topology: false, preservation }
    const [start, west, east] = parkway.vertices.map(v => v.position)
    const corner = orchard.vertices[1].position
    const centre = [290, 20]
    const ring = [...samples({ curves: [parkway.curves[1]] }), ...samples(willow).reverse(), ...samples(orchard).reverse()]
    const midpoint = at(parkway.curves[0], 0.5)
    const dx = west[0] - start[0]
    const dz = west[1] - start[1]
    const bend = Math.abs(dx * (midpoint[1] - start[1]) - dz * (midpoint[0] - start[0])) / Math.hypot(dx, dz)
    const siteFits = [parkway, orchard, willow].every(road => road.level === 0 && samples(road).every(([x, z]) =>
      x >= 200 && x <= 390 && z >= -75 && z <= 115 && Math.hypot(x - centre[0], z - centre[1]) >= 30))
    const expected = structuredClone(district)
    Object.assign(expected.junctions, { left: west, right: east, bottom: corner })
    Object.assign(expected.roads, {
      parkway: { kind: 'collector', level: 0, d: 'M east L left L right' },
      orchard: { kind: 'local', level: 0, d: 'M left L bottom' },
      willow: { kind: 'local', level: 0, d: 'M bottom L right' },
    })
    return {
      target: siteFits && bend >= 4 && parkway.kind === 'collector' && orchard.kind === 'local' && willow.kind === 'local'
        && west[0] < 290 && east[0] > 290 && west[1] < 20 && east[1] < 20 && corner[1] > 20 && encloses(ring, centre),
      topology: sameJoins(city, network(expected)),
      preservation,
    }
  },
  followup: 'The new survey sets the grove clearance at 25 m. Revise the block you just drew: move only the southern corner shared by orchard and willow 4 m north, towards the grove. Upgrade orchard to collector. Keep all other corners, all curve controls and all connections fixed.',
  gradeFollowup(before, after) {
    const expected = structuredClone(before.roads)
    expected.orchard.kind = 'collector'
    const [x, z] = expected.orchard.curves[0].at(-1)
    expected.orchard.curves[0][expected.orchard.curves[0].length - 1] = [x, z - 4]
    expected.willow.curves[0][0] = [x, z - 4]
    const revised = ['orchard', 'willow']
    return {
      target: revised.every(id => sameRoad(after.roads[id], expected[id])
        && samples(after.roads[id]).every(([x, z]) => Math.hypot(x - 290, z - 20) >= 25)),
      topology: sameJoins(before, after),
      preservation: Object.keys(after.roads).length === Object.keys(expected).length
        && Object.keys(expected).filter(id => !revised.includes(id)).every(id => sameRoad(after.roads[id], expected[id])),
    }
  },
}

export const cases = [moveMarket, softenBend, changeClass, relocateBlock, newBlock, grove]

export function promptFor(instruction, representation, operation, doc) {
  const positions = representation === 'coordinates'
    ? 'Write every endpoint as an absolute x,z coordinate pair. Endpoints at the same coordinates and level are the same junction.'
    : 'Write each endpoint as an ID in the junctions table. Reuse one ID to connect streets on the same level. To move a junction, change its [x,z] pair. Every junction must be used.'
  const writes = operation === 'rebuild'
    ? 'You can remove and add rows. To change any existing row, list its ID in remove and supply its entire replacement row in put. The batch takes effect together.'
    : 'You can edit in place. In put, supply only the fields of a road you wish to change, the pair for a junction you move, or a complete row for a new ID. Removing rows is optional.'
  return [
    'Edit this small city in a text-only simulation. x increases east and z increases south, in metres. Return just one JSON object. Do not use tools.',
    'Each keyed road has kind (arterial, collector, local), level (0 ground, 1 elevated) and a path d. Use one absolute M followed by one or more absolute L, Q or C commands, separated by spaces. Q takes one control pair and C takes two control pairs before the endpoint. Controls always use coordinates. Intermediate command endpoints are junctions. Crossing lines without a shared endpoint stay separate.',
    positions, writes,
    'Output {"put":{TABLE:{ID:VALUE}},"remove":{TABLE:[ID]}}. TABLE names are the tables in the input. Omit empty tables or operations. Use lowercase IDs. Leave unrelated streets intact.',
    JSON.stringify(doc), instruction,
  ].join('\n')
}
