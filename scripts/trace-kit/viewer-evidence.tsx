import { useEffect, useState } from 'react'
import { JSONTree } from 'react-json-tree'
import { Virtuoso } from 'react-virtuoso'
import type { ViewerClient } from './viewer-client'
import type { Attachment, Evidence, Frame } from './viewer-session'

const theme = {
  scheme: 'airimayor',
  author: 'airimayor',
  base00: '#141b27',
  base01: '#1b2433',
  base02: '#263043',
  base03: '#718097',
  base04: '#8c9ab0',
  base05: '#dce4f1',
  base06: '#e8effa',
  base07: '#ffffff',
  base08: '#ff8f9c',
  base09: '#e6b76c',
  base0A: '#e6b76c',
  base0B: '#87d8b4',
  base0C: '#5dd8e7',
  base0D: '#ad94ff',
  base0E: '#ad94ff',
  base0F: '#e6b76c',
}
export function EvidenceTree({ data }: { data: unknown }) {
  return (
    <div className="evidence-tree">
      <div className="tree-actions">
        <button className="button subtle" onClick={() => download(data)}>
          Download JSON
        </button>
      </div>
      <JSONTree
        data={data}
        theme={theme}
        invertTheme={false}
        collectionLimit={25}
        shouldExpandNodeInitially={(_keys, _value, level) => level < 1}
        postprocessValue={(value) =>
          typeof value === 'string' && value.length > 2000
            ? `${value.slice(0, 2000)}… [download JSON for the full value]`
            : value
        }
      />
    </div>
  )
}

function download(data: unknown) {
  const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }))
  const link = document.createElement('a')
  link.href = url
  link.download = 'evidence.json'
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

function Media({ attachment }: { attachment: Attachment }) {
  const [url, setUrl] = useState('')
  useEffect(() => {
    const url = URL.createObjectURL(attachment.blob)
    setUrl(url)
    return () => URL.revokeObjectURL(url)
  }, [attachment])
  return (
    <figure className="attachment">
      {attachment.type.startsWith('image/') && <img src={url} alt={attachment.name} loading="lazy" />}
      {attachment.type.startsWith('audio/') && <audio src={url} controls preload="none" />}
      {attachment.type.startsWith('video/') && <video src={url} controls preload="none" />}
      <figcaption>
        <a href={url} download={attachment.name}>
          {attachment.name}
        </a>
      </figcaption>
    </figure>
  )
}

function RawRecord({ client, index }: { client: ViewerClient; index: number }) {
  const [record, setRecord] = useState<unknown>()
  const [error, setError] = useState('')
  useEffect(() => {
    let current = true
    void client.remote.rawRecords([index]).then(
      (records) => {
        if (current) setRecord(records[0])
      },
      (error) => {
        if (current) setError(String(error))
      },
    )
    return () => {
      current = false
    }
  }, [client, index])
  return (
    <div className="raw-record">
      <h3>Record #{index + 1}</h3>
      {error || (record ? <EvidenceTree data={record} /> : 'Loading record…')}
    </div>
  )
}

export function EvidencePanel({ client, frame, raw }: { client: ViewerClient; frame: Frame; raw: boolean }) {
  const [evidence, setEvidence] = useState<Evidence>()
  const [error, setError] = useState('')
  useEffect(() => {
    let current = true
    setEvidence(undefined)
    setError('')
    void client.remote.evidence(frame.selection, frame.position, raw).then(
      (value) => {
        if (current) setEvidence(value)
      },
      (error) => {
        if (current) setError(String(error))
      },
    )
    return () => {
      current = false
    }
  }, [client, frame, raw])
  if (error) return <p className="error">{error}</p>
  if (!evidence) return <p className="empty-detail">Reading evidence at the replay cursor…</p>
  if (raw)
    return (
      <>
        <p className="raw-count">
          {evidence.rawIndices.length.toLocaleString()} original records through this cursor. Expand a record to inspect
          it.
        </p>
        <Virtuoso
          className="raw-list"
          data={evidence.rawIndices}
          computeItemKey={(_index, value) => value}
          itemContent={(_index, value) => <RawRecord client={client} index={value} />}
        />
      </>
    )
  return (
    <>
      {evidence.sections.length === 0 && (
        <p className="empty-detail">No evidence for this selection has reached the cursor.</p>
      )}
      {evidence.sections.map((section, index) => (
        <section className="evidence-section" key={`${section.title}:${index}`}>
          <h3>{section.title}</h3>
          <EvidenceTree data={section.data} />
        </section>
      ))}
      <div className="attachments">
        {evidence.attachments.map((attachment) => (
          <Media key={attachment.name} attachment={attachment} />
        ))}
      </div>
    </>
  )
}
