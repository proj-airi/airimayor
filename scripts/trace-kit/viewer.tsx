import { proxy } from 'comlink'
import { sortedLastIndex } from 'es-toolkit/compat'
import { useEffect, useMemo, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { Virtuoso, type VirtuosoHandle } from 'react-virtuoso'
import { Playback } from './playback'
import { ViewerClient } from './viewer-client'
import { EvidencePanel, EvidenceTree } from './viewer-evidence'
import { ExecutionTimeline } from './viewer-timeline'
import type { LoadProgress } from './viewer-model'
import type { Frame, Selection, SessionSummary } from './viewer-session'

type PickerWindow = Window & {
  showDirectoryPicker?: (options: { mode: 'read' }) => Promise<Parameters<ViewerClient['remote']['openDirectory']>[0]>
}
const number = (value: number) => value.toLocaleString()
const elapsed = (value: number) => `${(value / 1000).toFixed(3)}s`
const phases = {
  read: 'Reading records',
  records: 'Validating records',
  assets: 'Validating attachments',
  timeline: 'Building timeline',
}

function App() {
  const [client, setClient] = useState<ViewerClient>()
  const [summary, setSummary] = useState<SessionSummary>()
  const [frame, setFrame] = useState<Frame>()
  const [loading, setLoading] = useState(false)
  const [progress, setProgress] = useState<LoadProgress>()
  const [error, setError] = useState('')
  const [playing, setPlaying] = useState(false)
  const [follow, setFollow] = useState(true)
  const [speed, setSpeed] = useState(1)
  const [tab, setTab] = useState<'replay' | 'evidence' | 'raw'>('replay')
  const [query, setQuery] = useState('')
  const [matches, setMatches] = useState<number[]>([])
  const fileInput = useRef<HTMLInputElement>(null),
    recordInput = useRef<HTMLInputElement>(null)
  const eventList = useRef<VirtuosoHandle>(null)
  const activeClient = useRef<ViewerClient>()
  const frameRef = useRef(frame),
    followRef = useRef(follow),
    request = useRef(0),
    importing = useRef(0)
  const seeking = useRef(false)
  const desiredSeek = useRef<{ position: number; selection?: Selection; generation: number }>()
  frameRef.current = frame
  followRef.current = follow
  const player = useMemo(
    () =>
      client && summary
        ? new Playback<Frame>(summary.playTimes, {
            read: (position) => client.remote.frame(position, frameRef.current?.selection, followRef.current),
            commit: (value) => setFrame(value),
            state: setPlaying,
            error: (error) => setError(String(error)),
          })
        : undefined,
    [client, summary],
  )
  useEffect(
    () => () => {
      player?.pause()
    },
    [player],
  )
  useEffect(() => () => activeClient.current?.dispose(), [])
  useEffect(() => {
    if (!client || !summary) return
    let current = true
    void client.remote.searchEvents(query).then(
      (values) => {
        if (current) setMatches(values)
      },
      (error) => {
        if (current) setError(String(error))
      },
    )
    return () => {
      current = false
    }
  }, [client, summary, query])
  useEffect(() => {
    if (!follow || !frame || !matches.length) return
    const index = sortedLastIndex(matches, frame.position) - 1
    if (matches[index] === frame.position)
      eventList.current?.scrollIntoView({ index, align: 'center', behavior: 'auto' })
  }, [frame?.position, follow, matches])

  async function seek(position: number, selection?: Selection) {
    if (!client) return
    player?.pause()
    desiredSeek.current = { position, selection, generation: ++request.current }
    if (seeking.current) return
    seeking.current = true
    try {
      while (desiredSeek.current && activeClient.current === client) {
        const desired = desiredSeek.current
        desiredSeek.current = undefined
        const next = await client.remote.frame(
          desired.position,
          desired.selection ?? frameRef.current?.selection,
          desired.selection ? false : followRef.current,
        )
        if (desired.generation === request.current && activeClient.current === client) setFrame(next)
      }
    } catch (error) {
      if (activeClient.current === client) setError(String(error))
    } finally {
      seeking.current = false
    }
  }
  async function open(
    load: (client: ViewerClient, progress: (value: LoadProgress) => Promise<void>) => Promise<SessionSummary>,
  ) {
    player?.pause()
    request.current++
    desiredSeek.current = undefined
    seeking.current = false
    const generation = ++importing.current
    activeClient.current?.dispose()
    activeClient.current = undefined
    setClient(undefined)
    setSummary(undefined)
    setFrame(undefined)
    setProgress(undefined)
    setLoading(true)
    setError('')
    setQuery('')
    setMatches([])
    try {
      const next = new ViewerClient((error) => {
        if (generation === importing.current) {
          setError(error)
          setLoading(false)
        }
      })
      activeClient.current = next
      const result = await load(
        next,
        proxy(async (update: LoadProgress) => {
          if (generation === importing.current) setProgress(update)
        }),
      )
      if (generation !== importing.current) return
      const selection =
        [...result.items].reverse().find((item) => item.kind === 'model')?.selection ?? result.items[0]?.selection
      const initial = await next.remote.frame(result.events.length - 1, selection, false)
      if (generation !== importing.current) return
      setClient(next)
      setSummary(result)
      setFrame(initial)
      setTab('replay')
      setFollow(true)
      setLoading(false)
    } catch (error) {
      if (generation === importing.current) {
        setError(String(error))
        setLoading(false)
        activeClient.current?.dispose()
        activeClient.current = undefined
      }
    }
  }
  async function chooseFolder() {
    const picker = (window as PickerWindow).showDirectoryPicker
    if (!picker) {
      fileInput.current?.click()
      return
    }
    try {
      const directory = await picker.call(window, { mode: 'read' })
      void open((client, progress) => client.remote.openDirectory(directory, progress))
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError')) setError(String(error))
    }
  }
  function filesSelected(files: FileList | null) {
    if (!files?.length) return
    const selected = Array.from(files, (file) => ({ path: file.webkitRelativePath || file.name, file }))
    void open((client, progress) => client.remote.openFiles(selected, progress))
  }
  function togglePlay() {
    if (!player || !frame || !summary) return
    request.current++
    desiredSeek.current = undefined
    if (playing) player.pause()
    else {
      setTab('replay')
      player.play(frame.position === summary.events.length - 1 ? 0 : frame.position, speed)
    }
  }
  function inspect(next: typeof tab) {
    player?.pause()
    request.current++
    desiredSeek.current = undefined
    setTab(next)
  }
  const inputProps = {
    type: 'file',
    hidden: true,
    onChange: (event: React.ChangeEvent<HTMLInputElement>) => {
      filesSelected(event.target.files)
      event.target.value = ''
    },
  }
  return (
    <>
      <header className="app-header">
        <div className="brand">
          <span className="brand-mark">a</span>
          <div>
            <strong>AIRI MAYOR</strong>
            <span>SESSION REPLAY</span>
          </div>
        </div>
        <div className="header-title">
          <span className="eyebrow">LOCAL RECORDING</span>
          <h1>{summary?.name ?? 'Follow the mayor’s work.'}</h1>
        </div>
        <div className="import-actions">
          <button className="button primary" disabled={loading} onClick={() => void chooseFolder()}>
            Open record folder
          </button>
          <button className="button subtle" disabled={loading} onClick={() => recordInput.current?.click()}>
            Open records.jsonl
          </button>
        </div>
        <input
          {...inputProps}
          ref={fileInput}
          {...{ webkitdirectory: '', multiple: true }}
          aria-label="Select session record folder"
        />
        <input {...inputProps} ref={recordInput} accept=".jsonl" aria-label="Select records.jsonl" />
      </header>
      <main>
        {!summary && !loading && !error && (
          <section className="empty-state">
            <span className="eyebrow">EVERY STEP, IN CONTEXT</span>
            <h2>See how a session unfolded.</h2>
            <p>
              Follow model requests and city tools.
              <br />
              Replay the output in its recorded order.
            </p>
            <button className="button primary" onClick={() => void chooseFolder()}>
              Choose a session folder ↗
            </button>
            <p className="local-note">
              Select the folder containing <code>records.jsonl</code> and <code>assets</code>.<br />
              Everything stays in this browser. No server or network is used.
            </p>
          </section>
        )}
        {error && (
          <p id="import-status" className="error" role="alert">
            {error}
          </p>
        )}
        {loading && (
          <section className="import-loading" role="status">
            <p>
              {progress
                ? `${phases[progress.phase]} · ${number(progress.completed)} / ${number(progress.total)}`
                : 'Opening recording…'}
            </p>
            <progress id="import-progress" max={progress?.total || 1} value={progress?.completed ?? 0} />
            <button
              className="button subtle"
              onClick={() => {
                importing.current++
                activeClient.current?.dispose()
                activeClient.current = undefined
                setLoading(false)
              }}
            >
              Cancel import
            </button>
          </section>
        )}
        {summary && frame && client && (
          <section>
            <div className="session-summary">
              <div className="stats">
                <span>
                  <b>{number(summary.records)}</b> records
                </span>
                <span>
                  <b>{number(summary.models)}</b> model requests
                </span>
                <span>
                  <b>{number(summary.tools)}</b> city tools
                </span>
                <span>
                  <b>{elapsed(summary.duration)}</b> recorded
                </span>
              </div>
              <span className={`badge ${summary.issues.length ? 'warning' : 'success'}`}>
                {summary.issues.length ? `${number(summary.issues.length)} evidence issues` : 'Evidence validated'}
              </span>
            </div>
            {!!summary.issues.length && (
              <details className="issues">
                <summary>{number(summary.issues.length)} validation issues</summary>
                <Virtuoso
                  style={{ height: 200 }}
                  data={summary.issues}
                  itemContent={(_index, issue) => <p className="issue-row">{issue}</p>}
                />
              </details>
            )}
            <section className="playback panel" aria-label="Replay controls">
              <div className="transport">
                <div className="transport-buttons">
                  <button className="icon-button" aria-label="First event" onClick={() => void seek(0)}>
                    |◀
                  </button>
                  <button
                    className="icon-button"
                    aria-label="Previous event"
                    onClick={() => void seek(frame.position - 1)}
                  >
                    ‹
                  </button>
                  <button className="button play-button" onClick={togglePlay}>
                    {playing ? 'Ⅱ Pause' : '▶ Play'}
                  </button>
                  <button className="icon-button" aria-label="Next event" onClick={() => void seek(frame.position + 1)}>
                    ›
                  </button>
                  <button
                    className="icon-button"
                    aria-label="Last event"
                    onClick={() => void seek(summary.events.length - 1)}
                  >
                    ▶|
                  </button>
                </div>
                <span className="cursor-label">
                  {number(frame.position + 1)} / {number(summary.events.length)} · {elapsed(frame.time)}
                </span>
                <div className="transport-options">
                  <label>
                    Speed{' '}
                    <select
                      value={speed}
                      onChange={(event) => {
                        const value = Number(event.target.value)
                        setSpeed(value)
                        if (playing) player?.play(frame.position, value)
                      }}
                    >
                      {[0.5, 1, 2, 4, 8].map((value) => (
                        <option key={value} value={value}>
                          {value}×
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="follow">
                    <input type="checkbox" checked={follow} onChange={(event) => setFollow(event.target.checked)} />{' '}
                    Follow cursor
                  </label>
                </div>
              </div>
              <input
                type="range"
                min={0}
                max={summary.events.length - 1}
                value={frame.position}
                aria-label="Replay event position"
                onChange={(event) => void seek(Number(event.target.value))}
              />
              <div className="range-labels">
                <span>0.000s</span>
                <span>Recorded order · long gaps capped at 1.5 seconds during playback</span>
                <span>{elapsed(summary.duration)}</span>
              </div>
            </section>
            <ExecutionTimeline
              summary={summary}
              frame={frame}
              seek={(position, selection) => void seek(position, selection)}
            />
            <div className="detail-layout">
              <section className="event-panel panel">
                <div className="panel-heading">
                  <h2>
                    Event order <small>{number(matches.length)}</small>
                  </h2>
                  <span className="eyebrow">CONTINUOUS SCROLL</span>
                </div>
                <label className="search-label">
                  <span>⌕</span>
                  <input
                    type="search"
                    placeholder="Filter events or agents…"
                    value={query}
                    onChange={(event) => setQuery(event.target.value)}
                    aria-label="Filter events or agents"
                  />
                </label>
                <div
                  className="event-scroll"
                  onWheel={() => {
                    if (follow) setFollow(false)
                  }}
                >
                  <Virtuoso
                    ref={eventList}
                    data={matches}
                    computeItemKey={(_index, position) => summary.events[position].index}
                    itemContent={(_index, position) => {
                      const row = summary.events[position]
                      return (
                        <button
                          className={`event-row ${position === frame.position ? 'active' : ''}`}
                          onClick={() => void seek(position, row.selection)}
                        >
                          <span className="event-time">{elapsed(row.time)}</span>
                          <span className="event-copy">
                            <strong>{row.title}</strong>
                            <small>
                              {row.agent} · #{number(row.index + 1)}
                            </small>
                          </span>
                          <span className={`event-kind ${row.kind}`}>{row.kind}</span>
                        </button>
                      )
                    }}
                    components={{ EmptyPlaceholder: () => <p className="event-empty">No matching events.</p> }}
                  />
                </div>
              </section>
              <section className="inspector panel">
                <div className="inspector-header">
                  <div className="breadcrumb">{frame.breadcrumb.join(' › ')}</div>
                  <h2>{frame.heading}</h2>
                  <span className="badge">{frame.state}</span>
                </div>
                <div className="inspector-tabs" role="tablist">
                  {(['replay', 'evidence', 'raw'] as const).map((value) => (
                    <button key={value} role="tab" aria-selected={tab === value} onClick={() => inspect(value)}>
                      {value === 'replay' ? 'Output replay' : value === 'evidence' ? 'Full evidence' : 'Raw records'}
                    </button>
                  ))}
                </div>
                <div className="inspector-body" role="tabpanel">
                  {tab === 'replay' ? (
                    <>
                      <p className="output-meta">
                        {frame.channel
                          ? `${number(frame.chunks)} recorded output updates through this cursor`
                          : 'Select a model request to replay its output. Open Full evidence to inspect a tool or event.'}
                      </p>
                      {frame.reasoning && (
                        <details className="reasoning">
                          <summary>Reasoning at cursor</summary>
                          <pre>{frame.reasoning}</pre>
                        </details>
                      )}
                      {frame.text && <pre className="output-text">{frame.text}</pre>}
                      {frame.completedOutput !== undefined && <EvidenceTree data={frame.completedOutput} />}
                      {frame.channel && !frame.chunks && frame.completedOutput === undefined && (
                        <p className="empty-detail">No output has reached this cursor.</p>
                      )}
                    </>
                  ) : (
                    <EvidencePanel client={client} frame={frame} raw={tab === 'raw'} />
                  )}
                </div>
              </section>
            </div>
          </section>
        )}
      </main>
      <footer>
        <span>airimayor · trace-kit</span>
        <span>Local evidence. Recorded facts.</span>
      </footer>
    </>
  )
}

createRoot(document.getElementById('app')!).render(<App />)
