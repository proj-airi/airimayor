import { createTimelineProjection, TraceReader } from '@proj-airi/trace-kit/projections'
import type { TraceRecord } from '@proj-airi/trace-kit'

export function isOverviewRecord(record: TraceRecord) {
  return record.type !== 'instant' || (record.name !== 'model.output.delta' && record.name !== 'acp.output.delta')
}

export function createSessionViewReader(clock: string) {
  const timeline = createTimelineProjection({ seriesRetention: false })
  const project = timeline.handleRecord
  // Output chunks stay indexed for replay; overview instants describe structural events.
  timeline.handleRecord = (state, record, context) =>
    isOverviewRecord(record) ? project(state, record, context) : state
  const reader = new TraceReader([timeline], clock)
  return { reader, timelineView: () => reader.view(timeline) }
}
