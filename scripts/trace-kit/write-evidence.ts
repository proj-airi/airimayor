export const LEDGER_FIELDS = ['money', 'developmentPoints', 'buildings', 'networkEdges', 'transitLines']
const WITNESS_FIELDS: Record<string, string[]> = {
  place_building: ['buildings', 'money'],
  build_network: ['networkEdges', 'money'],
  replace_road_type: ['networkEdges', 'money'],
  set_road_features: ['money', 'networkEdges'],
  demolish: ['buildings', 'networkEdges'],
  set_facility_upgrade: ['buildings', 'money'],
  add_transit_line: ['transitLines'],
  remove_transit_line: ['transitLines'],
  expand_operational_area: ['money'],
  buy_tiles: ['money'],
  purchase_development_node: ['developmentPoints'],
}
const UNSEEN = new Set(['zone', 'set_budget', 'set_policy'])
export type Verdict = 'confirmed' | 'diverged' | 'undetermined'

function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

export function judgeWrite(
  tool: string,
  data: Record<string, unknown>,
): { verdict?: Verdict; message: string; invalid?: boolean } {
  if (!(tool in WITNESS_FIELDS) && !UNSEEN.has(tool)) return { message: '' }
  const claim = data.success ? 'succeeded' : 'failed'
  if (UNSEEN.has(tool)) return { verdict: 'undetermined', message: `${claim}; the ledger does not see this write` }
  if (!data.evidence) return { verdict: 'undetermined', message: `${claim}; the record holds no ledger evidence` }
  const { before, after } = object(data.evidence) ? data.evidence : {}
  if (!object(before) || !object(after))
    return { invalid: true, message: 'ledger evidence needs before and after objects' }
  const moved = LEDGER_FIELDS.filter((field) => before[field] !== after[field])
  const change = moved.map((field) => `${field} ${before[field]}->${after[field]}`).join(', ') || 'nothing moved'
  if (!(before.paused && after.paused && before.frame === after.frame))
    return {
      verdict: 'undetermined',
      message: `${claim}; simulation ran (frames ${before.frame}->${after.frame}); ${change}`,
    }
  if (data.success)
    return WITNESS_FIELDS[tool].some((field) => moved.includes(field))
      ? { verdict: 'confirmed', message: `succeeded; ${change}` }
      : { verdict: 'undetermined', message: `reported success; ledger cannot confirm the requested change; ${change}` }
  return moved.length
    ? { verdict: 'diverged', message: `reported failure but the city changed; ${change}` }
    : { verdict: 'confirmed', message: 'failed; nothing moved' }
}
