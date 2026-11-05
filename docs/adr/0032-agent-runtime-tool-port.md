# Agent runtime beside the game tool port

Status: the tool port remains accepted; built-in loop, plan and steering policies are superseded by [0048](0048-native-maf-harness.md). The shared library is now `Agent/airimayor.Agent.csproj`; the original name below is historical.

The in-process loop lives in `AgentRuntime`, a library the mod references. The game supplies tools through `IAgentTools` (`List` and `InvokeAsync` only) and passes the system prompt once, when the runtime is created. `set_plan` stays inside the runtime as a generic plan. The runtime does not reference `Game` or Colossal, and it does not name city tools. A player message stops a reply still being generated and waits for a tool that has already started, including a time advance. `Cancel` stops the turn. If that cancels an advance, the city tool restores the clock. This replaces the sentence in [ADR-0030](0030-player-message-keeps-plan.md) and [ADR-0031](0031-explicit-pause.md) that a player message ends a time advance.

Gameface still calls `Prompt`, `Cancel`, and the update stream on the built-in runtime. A later head swap keeps that chat and points the same three calls at an external runtime over ACP (`session/prompt`, `session/cancel`, `session/update`). City tools stay on this port and are handed to that runtime as MCP. This change implements neither ACP nor MCP.

## Considered Options

- **Keep the loop inside the mod assembly.** Rejected: the same project references `Game.dll`, so a city type can leak back into the reusable loop.
- **Mirror the ACP client and agent method lists now.** Rejected: one built-in runtime exists, and ACP does not carry city tools. Those tools are an MCP server on `session/new`.
- **Move the plan out of the runtime.** Rejected: declaring and keeping a plan is loop state, not a city tool.
- **Let a player message cancel one host-marked tool call.** Rejected: that mark is not a tool-port fact, and an external runtime cannot see it. Stopping the turn stays on `Cancel`. The advance tool still restores the clock when its own call is cancelled.
