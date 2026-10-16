# One session, one record

Status: accepted

Each chat session writes one self-contained directory under `ModsData/airimayor/sessions/<startedUtc>--<session>/`. It holds `session.json`, `timeline.jsonl`, `model-calls.jsonl`, the tool catalogs the model saw (`catalogs/<sha256>.json`), the images and diagnostic files a tool call returned (`calls/<call>/`), and the records of owned conversations (`conversations/`, same layout). Tool handlers return files; only the record writes them. Live acceptance evidence is that directory, not a set of files gathered by hand. `session.json` carries the facts that occur once: head, mod version, city, model, prompt hash, parent session and turn for an owned conversation, start, and declared known gaps. `ended` is written last. A record without `ended` was cut short.

Every timeline line has the same envelope: `schema`, `seq`, `ts` (UTC), `session`, `turn`, an optional `call`, and `clock`. `clock` is the simulation frame, in-game time, and selected speed, read on the simulation thread. An event inside a turn carries that turn. An event between turns carries none, so a late city warning is never pinned to the last turn. Call ids are passed with each event. A tool call writes `function.start` before it runs and `function` when it ends. The `function` event has four anchors: `submitted` and `completed` are read from the clock, and `applied` is the frame where the simulation thread ran the request. `queuedMs` is the real time that request waited in the queue.

`model-calls.jsonl` has one line per model request, compaction included: the client-side messages and the catalog hash, the outcome (text, reasoning, tool calls with ids, usage, failure), and the `submitted` and `completed` anchors. The external mayor records turns, city tool calls, and tool rows with the same envelope. Its model calls are not observable, which `session.json` declares as a known gap. Gaps are named, never filled in. Examples: the recorded messages are client messages, not wire bytes, and a rotated-out file is listed. Unknown is never written as empty.

## Considered Options

- **Keep one flat `agent-timeline-<session>.jsonl` and add fields.** Rejected: screenshots, map dumps, and owned conversations stay scattered, and a cut-short session looks like a clean one.
- **OpenTelemetry spans to a collector.** Rejected: the player runs acceptance offline and pastes files; a collector is a second process to install.
- **Protobuf schemas, as in recorder-minecraft.** Rejected for now: one C# writer and one Python reader do not justify a code generator. `schema` is versioned so a later contract can reject old shapes.
