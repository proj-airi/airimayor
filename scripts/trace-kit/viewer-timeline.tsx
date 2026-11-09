import { useEffect, useRef } from 'react'
import { Timeline, type TimelineGroup, type DataItem } from 'vis-timeline/standalone'
import moment, { type MomentInput } from 'moment'
import { sortedLastIndex } from 'es-toolkit/compat'
import type { Frame, Selection, SessionSummary } from './viewer-session'

export function ExecutionTimeline({
  summary,
  frame,
  seek,
}: {
  summary: SessionSummary
  frame: Frame
  seek: (position: number, selection?: Selection) => void
}) {
  const container = useRef<HTMLDivElement>(null)
  const timeline = useRef<Timeline>()
  const onSeek = useRef(seek)
  onSeek.current = seek
  useEffect(() => {
    const text = (value: string) => {
      const element = document.createElement('span')
      element.textContent = value
      return element
    }
    const groups: TimelineGroup[] = summary.groups.map((group, order) => ({
      id: group.id,
      content: text(group.label),
      order,
      className: `group-${group.kind}`,
      nestedGroups: summary.groups.filter((child) => child.parent === group.id).map((child) => child.id),
      showNested: group.expanded,
    }))
    const items: DataItem[] = summary.items.map((item) => ({
      id: item.id,
      group: item.group,
      content: item.label,
      start: item.start,
      end: Math.max(item.start + 1, item.end),
      type: 'range',
      className: `call-${item.kind} status-${item.status}`,
      title: `${item.label} · ${item.status} · ${((item.end - item.start) / 1000).toFixed(3)}s`,
    }))
    const view = new Timeline(container.current!, items, groups, {
      showCurrentTime: false,
      stack: true,
      maxHeight: 440,
      verticalScroll: true,
      zoomKey: 'ctrlKey',
      groupOrder: 'order',
      orientation: 'top',
      moment: (value: MomentInput) => moment(value).utc(),
      template: (item) => text(item.isCluster ? `${item.uiItems.length} calls` : item.content),
      start: 0,
      end: Math.max(1000, summary.duration),
      min: 0,
      max: Math.max(1000, summary.duration),
      zoomMin: 10,
      cluster: { maxItems: 5, titleTemplate: '{count} calls · zoom in to inspect', fitOnDoubleClick: true },
      format: {
        minorLabels: { millisecond: 'SSS', second: 'mm:ss', minute: 'HH:mm', hour: 'HH:mm' },
        majorLabels: { millisecond: 'HH:mm:ss', second: 'HH:mm', minute: '', hour: '' },
      },
    })
    view.addCustomTime(0, 'cursor')
    view.setCustomTimeTitle('Replay cursor', 'cursor')
    view.on('select', ({ items: selected }) => {
      const item = summary.items.find((item) => item.id === selected[0])
      if (item) onSeek.current(item.position, item.selection)
    })
    view.on('click', (event) => {
      if (event.what !== 'background' || !event.time) return
      const index = Math.max(0, sortedLastIndex(summary.seekTimes, event.time.valueOf()) - 1)
      onSeek.current(summary.seekPositions[index])
    })
    timeline.current = view
    return () => {
      view.destroy()
      timeline.current = undefined
    }
  }, [summary])
  useEffect(() => {
    timeline.current?.setCustomTime(frame.time, 'cursor')
  }, [frame.time])
  const selectedKey = frame.selection?.kind === 'span' ? frame.selection.key : undefined
  const selected = summary.items.some((item) => item.id === selectedKey) ? selectedKey : undefined
  useEffect(() => {
    timeline.current?.setSelection(selected ? [selected] : [])
  }, [selected])
  return (
    <section className="timeline-panel panel">
      <div className="panel-heading">
        <h2>Execution timeline</h2>
        <div className="timeline-controls">
          <button className="button subtle" onClick={() => timeline.current?.fit()}>
            Fit all
          </button>
          <button className="icon-button" aria-label="Zoom out" onClick={() => timeline.current?.zoomOut(0.5)}>
            −
          </button>
          <button className="icon-button" aria-label="Zoom in" onClick={() => timeline.current?.zoomIn(0.5)}>
            +
          </button>
          <button
            className="button subtle"
            disabled={!selected}
            onClick={() => selected && timeline.current?.focus(selected)}
          >
            Focus call
          </button>
        </div>
      </div>
      <div ref={container} className="execution-timeline" aria-label="Nested execution timeline" />
      <div className="timeline-footnote">
        Turn → agent run → model requests / city tools. Drag to pan, Ctrl+scroll to zoom; double-click a cluster to
        inspect its calls. Times are elapsed from the recording start.
      </div>
    </section>
  )
}
