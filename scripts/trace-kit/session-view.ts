import { createTimelineProjection, TraceReader } from '@proj-airi/trace-kit/projections'

export function createSessionViewReader(clock: string) {
  const timeline = createTimelineProjection({ seriesRetention: false })
  const project = timeline.handleRecord
  // Output chunks stay indexed for replay; overview instants describe structural events.
  timeline.handleRecord = (state, record, context) => record.type === 'instant'
    && (record.name === 'model.output.delta' || record.name === 'acp.output.delta')
    ? state : project(state, record, context)
  const reader = new TraceReader([timeline], clock)
  return { reader, timelineView: () => reader.view(timeline) }
}
