import type { TraceRecord } from '@proj-airi/trace-kit'
import type { Recording } from './viewer-model'
import { spanKeyOf } from '@proj-airi/trace-kit'
import { payload } from './viewer-model'

export interface RecordedTurn {
  id: string
  label: string
  status: string
  positions: number[]
  start: number
  end: number
  models: number
  tools: number
}

/** Display scopes keep native identities and evidence; only their track presentation changes. */
export class RecordingScope {
  readonly turns: RecordedTurn[]

  constructor(private recording: Recording) {
    const positions = new Map(recording.events.map((index, position) => [index, position]))
    const spans = recording.full.timelineView().spans
    const origin = recording.full.reader.timeOf(recording.records[0]).time
    const lastByTurn = new Map<string, number>()
    const positionsByTurn = new Map<string, number[]>()
    recording.records.forEach((record, index) => {
      for (const id of new Set([record.ids?.turn, record.ids?.originTurn])) {
        if (id === undefined) continue
        const key = String(id)
        lastByTurn.set(key, index)
        const position = positions.get(index)
        if (position === undefined) continue
        const list = positionsByTurn.get(key) ?? []
        list.push(position)
        positionsByTurn.set(key, list)
      }
    })
    const callsByTurn = new Map<string, { models: number; tools: number }>()
    for (const span of spans.values()) {
      for (const id of new Set([span.ids?.turn, span.ids?.originTurn])) {
        if (id === undefined) continue
        const counts = callsByTurn.get(String(id)) ?? { models: 0, tools: 0 }
        counts.models += Number(span.track.endsWith('/llm'))
        counts.tools += Number(span.track.endsWith('/tools'))
        callsByTurn.set(String(id), counts)
      }
    }
    this.turns = recording.records.flatMap((record, index) => {
      if (record.type !== 'span.begin' || record.name !== 'chat-turn' || !record.ids?.turn) return []
      const id = String(record.ids.turn)
      const span = spans.get(spanKeyOf(record.source, record.epoch, record.spanId))!
      const last = lastByTurn.get(id) ?? index
      return [
        {
          id,
          label: String(payload(recording, record).user ?? 'Player request'),
          status: span.status ?? 'incomplete',
          positions: positionsByTurn.get(id)!,
          start: span.start - origin,
          end: recording.full.reader.timeOf(recording.records[last]).time - origin,
          ...(callsByTurn.get(id) ?? { models: 0, tools: 0 }),
        },
      ]
    })
  }

  private belongs(record: TraceRecord, turn: string) {
    return record.ids?.turn === turn || record.ids?.originTurn === turn
  }

  records(turn?: string) {
    return this.recording.records.filter(
      (record) =>
        !turn ||
        record.type === 'source.started' ||
        record.type === 'definition' ||
        record.type === 'clock.snapshot' ||
        this.belongs(record, turn),
    )
  }
}

export function presentRecords(records: readonly TraceRecord[]): TraceRecord[] {
  const clock = records[0].clock
  const definitions = [
    {
      path: 'agents/{agent}/turn',
      entity: 'agent',
      group: 'agents',
      order: 10,
      render: ['spans'],
      label: 'Mayor turns',
    },
    {
      path: 'agents/{agent}/execution',
      entity: 'agent',
      group: 'agents',
      order: 20,
      render: ['spans'],
      label: 'Agent execution',
    },
    { path: 'city/events', group: 'session', order: 0, render: ['instants'], label: 'Session events' },
    { path: 'city/diagnostics', group: 'session', order: 10, render: ['instants'], label: 'Diagnostics' },
  ]
  const execution = new Set(
    records.flatMap((record) =>
      (record.type === 'span.begin' || record.type === 'span.complete') &&
      record.name.startsWith('invoke_agent ') &&
      record.track.endsWith('/turn')
        ? [spanKeyOf(record.source, record.epoch, record.spanId)]
        : [],
    ),
  )
  const paths = new Set(definitions.map((definition) => definition.path))
  const native = records
    .filter((record) => !(record.type === 'definition' && record.kind === 'track' && paths.has(record.key)))
    .map((record): TraceRecord => {
      if (!('track' in record) || !('spanId' in record) || !record.spanId) return record
      const key = spanKeyOf(record.source, record.epoch, record.spanId)
      return execution.has(key)
        ? { ...record, track: record.track!.replace(/\/turn$/, '/execution') }
        : record
    })
  return [
    ...definitions.map(
      (data, seq): TraceRecord => ({
        type: 'definition',
        kind: 'track',
        key: data.path,
        data,
        source: 'viewer',
        epoch: 'presentation',
        seq,
        ts: 0,
        clock,
      }),
    ),
    ...native,
  ]
}
