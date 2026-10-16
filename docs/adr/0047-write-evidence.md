# Write evidence stays in the session record

Status: accepted

A model-facing write records city evidence next to its result: a city ledger read on the simulation thread before the write and again after it completes. The ledger holds treasury, building count, network edge count, transit line count, the frame, and whether the city was paused. The evidence goes into the `function` event of the session record ([0046](./0046-session-record.md)), not into the result the model reads. An offline check (`scripts/check-session.py`) compares each write's claim with its evidence. It also flags calls with no end, sessions with no end, and broken envelope ordering. Its verdict has three values: `confirmed`, `diverged` (the write reported success and nothing changed, or reported failure and something changed), and `undetermined` (the simulation ran during the write, so other causes can also move the counts).

## Considered Options

- **Append the before/after ledger to the write result.** Rejected: result notes are a city contract, and a bundled snapshot trains the model to skip fresh reads, as the advance result did ([0029](./0029-player-owned-clock.md)).
- **Per-tool proof, such as finding the placed building near its pose.** Deferred: it is exact but repeats each write's own search. The generic ledger covers every write first.
- **Verify in the loop and retry on divergence.** Rejected: construction recovery belongs inside the write tool, and the record is evidence, not control.
