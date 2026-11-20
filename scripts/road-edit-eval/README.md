# Road editing benchmark

This Vieval experiment measures whether a text agent can design and revise a district while preserving its neighbours. It runs outside Cities: Skylines II. It sends only synthetic street documents and editing instructions to the selected model. [Research and results](../../docs/research/2026-11-20-road-generation.md)

## Run

Use Node 22.12 or later and a signed-in Codex CLI that supports `exec --ephemeral --ignore-user-config --json`. On Windows the runner uses `codex.exe`. From this directory run:

```sh
npm ci
npm test
npm run eval -- --report-out .vieval/reports --task-concurrency 4 --case-concurrency 4
```

The default model is `gpt-6.2-sol` at low reasoning effort. Set `ROAD_MODEL` to another available Codex model for another comparison. Each call starts in a fresh temporary directory with read-only access. External tool use invalidates a trial. A model answer that violates the protocol scores zero. A runner or authentication error is reported separately. Vieval writes the prompts, answers, available token usage, verdicts and rendered SVGs below the report directory.

To score the archived pilot without a model or Codex login, set `ROAD_REPLAY` to the path of [pilot-2026-11-20.jsonl](./pilot-2026-11-20.jsonl) before running the same eval command. Each saved response is bound to the hash of the prompt that produced it, including the model's own street design on the second turn.

## What varies

| Factor | Conditions |
| --- | --- |
| Representation | Coordinate paths repeat endpoint positions. Equal command endpoints at the same level form a junction. Reference paths use IDs from a shared junction table. Curve controls use absolute coordinates in both formats. |
| Editing operation | Rebuild removes and adds a complete row to change it. Edit patches individual road fields or a junction coordinate pair. Both apply one batch to the same readable document. |

Every street has an ID, class, level and one path. Paths support space-separated absolute `M`, `L`, `Q` and `C`. For example, `M 0 0 Q 50 -24 100 0` and `M west Q 50 -24 market` draw the same curve if `west` and `market` name those endpoints. Paths can cross without joining. A named junction can join multiple paths at command boundaries. This is a subset of SVG path syntax in a JSON road document. The rebuild condition is an offline control that can use the same curves and perform atomic changes. The in-game segment interface has different capabilities.

## What counts

Six scenarios share a starting district with an arterial, a collector, a residential block and a flyover. Four require local revisions. A fifth adds a bounded block. In the sixth, the model chooses a curved alignment and encloses a protected grove. After a passing first drawing, a fresh model call reads that drawing and a second instruction to move a corner and change the street hierarchy. A failed first drawing fails the episode. Both repetitions use the same tasks and model settings.

The headline score requires the requested change, the expected junction memberships and preservation of existing streets outside the change. The scorer compares geometry and classes after applying the answer. It checks fixed curve shapes at four parameter values per segment with a 1 cm tolerance. Site and clearance checks sample each segment at 33 points. A top-down SVG is attached for inspection. The agent receives text only. Human aesthetic preference, native construction, collision, lane routing, stale city state and traffic flow are outside this experiment.
