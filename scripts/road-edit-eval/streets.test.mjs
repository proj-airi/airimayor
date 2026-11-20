import assert from 'node:assert/strict'
import test from 'node:test'
import { cases } from './cases.mjs'
import { applyChange, district, documentFor, network, sameJoins, sameRoad } from './streets.mjs'

test('coordinate and reference streets start with equal alignments, hierarchy and junctions', () => {
  const a = network(documentFor('coordinates'))
  const b = network(documentFor('references'))
  assert.ok(sameJoins(a, b))
  for (const id of Object.keys(district.roads)) assert.ok(sameRoad(a.roads[id], b.roads[id]))
})

test('a shared junction edit moves both approaches and a coincident disconnected reference fails topology', () => {
  const doc = applyChange(district, { put: { junctions: { market: [116, 8] } } }, 'edit')
  const move = cases.find(item => item.name === 'move-market')
  assert.deepEqual(move.grade(network(doc)), { target: true, topology: true, preservation: true })
  doc.junctions.duplicate = [116, 8]
  doc.roads.garden.d = 'M duplicate L gardens'
  assert.deepEqual(move.grade(network(doc)), { target: true, topology: false, preservation: true })
})

test('rebuild requires removal but can achieve the same end state as editing', () => {
  const put = { roads: { garden: { ...district.roads.garden, kind: 'local' } } }
  assert.throws(() => applyChange(district, { put }, 'rebuild'), /Remove/)
  assert.deepEqual(applyChange(district, { remove: { roads: ['garden'] }, put }, 'rebuild'),
    applyChange(district, { put: { roads: { garden: { kind: 'local' } } } }, 'edit'))
})

test('a damaged flyover fails preservation independently of the requested local class changes', () => {
  const doc = structuredClone(district)
  doc.roads.court.kind = 'collector'
  doc.roads.garden.kind = 'local'
  doc.roads.flyover.d = 'M pier Q 80 0 ridge'
  const scenario = cases.find(item => item.name === 'change-hierarchy')
  assert.deepEqual(scenario.grade(network(doc)), { target: true, topology: true, preservation: false })
})

test('a two-turn grove design admits different corners, rejects a shortcut and uses its own first drawing', () => {
  const scenario = cases.find(item => item.followup)
  for (const x of [285, 310]) {
    const doc = structuredClone(district)
    Object.assign(doc.junctions, { left: [230, -30], right: [355, -30], south: [x, 92] })
    Object.assign(doc.roads, {
      parkway: { kind: 'collector', level: 0, d: 'M east Q 212 -32 left L right' },
      orchard: { kind: 'local', level: 0, d: 'M left Q 213 86 south' },
      willow: { kind: 'local', level: 0, d: 'M south Q 375 85 right' },
    })
    const before = network(doc)
    assert.deepEqual(scenario.grade(before), { target: true, topology: true, preservation: true })
    doc.junctions.south = [x, 88]
    doc.roads.orchard.kind = 'collector'
    assert.deepEqual(scenario.gradeFollowup(before, network(doc)), { target: true, topology: true, preservation: true })
    doc.junctions.south = [290, 20]
    doc.roads.orchard.kind = 'local'
    assert.equal(scenario.grade(network(doc)).target, false)
  }
})
