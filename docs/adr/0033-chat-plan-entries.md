# Chat plan is one step list

Status: built-in planning superseded by [0048](0048-native-maf-harness.md). External ACP plan projection retains this display format.

The built-in plan and the chat plan strip are one [Agent Plan v1](https://agentclientprotocol.com/protocol/v1/agent-plan) list of steps. Each step has `content`, `priority` (`high`, `medium`, or `low`), and `status` (`pending`, `in_progress`, or `completed`). `set_plan` replaces the whole list. Its return value is the list the model sees. The loop does not pin a live note on later rounds. `goal` and `success` are gone. Agent Plan v2 `planId` is not used: the session holds one plan. This replaces the sentences in [ADR-0028](0028-mayor-mandate.md) that `goal` names the plan and that each model round pins one live note. Writes stay ungated, and player messages still leave the plan in place ([ADR-0030](0030-player-message-keeps-plan.md)).

## Considered Options

- **One projected entry from `goal` and `success`.** Rejected: the built-in plan would not be the same list an external agent sends.
- **Agent Plan v2, with `planId`.** Rejected: the chat holds one plan, not several at once.
- **A live note pinned every round.** Rejected: the model already sees the list as the `set_plan` result.
