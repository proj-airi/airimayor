# Built-in mayor uses native MAF Harness behavior

Status: accepted

The Responses-only transport choice below is superseded by [0050](0050-model-provider-connections.md). Native Harness behavior remains applicable.

The session-record capture omissions below are superseded by [0052](0052-trace-kit-session-records.md), which adopts native SDK observation and the trace-kit record format.

The built-in mayor now uses `Microsoft.Agents.AI.Harness` 1.27.0 over the Responses adapter, replacing our handwritten loop. The second prototype measured 2,067 fewer normalized lines (43.3%) across the runtime and all new client adapters; preserving the old diagnostics, gateway patches and scheduling rules had erased most of that benefit. We accept the library's planning, compaction, iteration and background-task behavior rather than implementing those policies again around Harness.

The shared agent module is `Agent/airimayor.Agent.csproj`, with namespace and assembly name `airimayor.Agent`. It contains session interfaces, Harness adaptation, records and shared tool access. Runtime names the mayor's complete operating environment; Harness is one part of it.

The model connection, instructions and city-tool catalog are fixed for one session; Apply opens a new session. `WindowTokens` remains player-set, with a 16,384-token output limit/reserve. Native compaction evicts old tool detail and truncates history; there is no custom summary schema, tail formula or context-error retry. Chat Completions, reasoning echo and conversation-header patches are removed. An incompatible provider reports its error through chat.

Harness owns todos and `background_agents_*` tools. The built-in plan strip projects todo titles and completion, with medium priority for presentation. Background tasks use start/wait/results/continue/clear and do not wake an idle root. Stop cancels the root run and its city tool, leaving background work running; closing the session cancels and awaits background work. No head has a continuous-run scheduler or automatic kickoff on city load: those settings, startup prompts and trigger paths are removed. External ACP planning and `task` report delivery remain external-head behavior.

Player text enters the native injection queue in arrival order and is read at the next model boundary; sending text does not cancel a generation or a tool. The client adapter owns scheduling, Stop, chat projection and city evidence. A small recovery decorator repairs incomplete tool-result history after failed/cancelled model calls, including native-tool results and failed child continuation; it does not replace the SDK loop. Separate streaming and non-streaming hooks await repair before exposing cancellation, avoiding the SDK's shared middleware channel race. Repairing history also invalidates the pinned SDK's compaction index, so its cached groups cannot omit restored player text or tool results on the next call. City permissions, simulation-thread dispatch, native construction validation and clock restoration stay in the existing game tool port.

Session records retain city-tool starts/results, before/after write evidence, files, simulation anchors and SDK warning/error diagnostics. Full model snapshots, catalog/usage capture and separate native child records are removed; these gaps are declared in `session.json`. Neither head creates `model-calls.jsonl` or catalogs; the writer and offline checker contain no model-capture path. Native child city calls share the parent record with session-qualified call ids and a caller name. They do not imply a separate child transcript or full parent/child causal trace.

This supersedes the handwritten-loop choice in [0001](0001-in-process-meai-loop.md), built-in planning/iteration rules in [0028](0028-mayor-mandate.md) and [0033](0033-chat-plan-entries.md), built-in steering in [0030](0030-player-message-keeps-plan.md) and [0032](0032-agent-runtime-tool-port.md), built-in delegation in [0037](0037-owned-conversations.md) and [0038](0038-one-task-tool.md), and the built-in model/child capture requirement in [0046](0046-session-record.md). The game tool port and external ACP contracts remain applicable.

## Considered options

- Keep the handwritten loop: rejected because native Harness now covers the generic orchestration with a smaller maintained implementation.
- Adopt Harness while reproducing every old rule: rejected after the first prototype saved only 730 normalized lines (15.3%) and left the main runtime larger.
- Native behavior without cancellation recovery: rejected because a failed next model call can leave completed tool results out of stored history in the pinned SDK.
