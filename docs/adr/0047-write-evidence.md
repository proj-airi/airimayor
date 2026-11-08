# Write evidence stays in the session record

Status: accepted

Recording format and offline tooling are revised by [0052](0052-trace-kit-session-records.md). Evidence now lives on the trace's city-tool result; use `npm --prefix scripts/trace-kit run check -- "<session directory>"`.

A model-facing write records city evidence next to its result: a city ledger read on the simulation thread before the write and again after it completes. The ledger holds the treasury, development points, the building, network edge, and transit line counts, the frame, and whether the city was paused. The evidence goes onto `city.tool.result`, not into the result the model reads. The offline checker compares each write's claim with the fields that can witness that write. It also flags unfinished spans and sources, broken record ordering, and missing assets.

`confirmed` means a paused ledger contains a compatible change, or stays unchanged for a reported failure. `diverged` means a write reported failure while the paused ledger changed. `undetermined` covers missing evidence, a running simulation, writes outside the ledger's coverage, and successful writes without a matching counter change. A free operational-area expansion need not spend money; extending a road can merge adjacent edges without increasing their count. Unchanged counters cannot contradict those successes. These verdicts describe the ledger's evidence, not proof of the requested geometry or capacity. Per-tool readback can establish those facts separately.

## Considered Options

- **Append the before/after ledger to the write result.** Rejected: result notes are a city contract, and a bundled snapshot trains the model to skip fresh reads, as the advance result did ([0029](./0029-player-owned-clock.md)).
- **Per-tool proof, such as finding the placed building near its pose.** Deferred: it is exact but repeats each write's own search. The generic ledger covers every write first.
- **Verify in the loop and retry on divergence.** Rejected: construction recovery belongs inside the write tool, and the record is evidence, not control.
