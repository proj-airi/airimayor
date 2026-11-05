# Player messages keep the plan

Status: built-in steering superseded by [0048](0048-native-maf-harness.md). Player messages still keep the plan. Earlier advance behavior was replaced by [0032](0032-agent-runtime-tool-port.md).

A player message used to clear the mayor mandate and cancel the whole turn, including a tool already running. The plan now stays until the model calls `set_plan` again. The latest player message constrains that plan. A message during a reply cancels only that reply. A message during a tool step waits until the tools already requested in that step finish, then the message is the next turn. A message during a time advance ends that advance, restores the previous clock, and does not start later tools in that step. Stop still cancels the turn, including tools. Autonomous continuation also leaves the plan in place. This replaces the “player messages clear it” sentence in [ADR-0028](0028-mayor-mandate.md).

## Considered Options

- **Clear the plan on every player message.** Rejected: the player is constraining the current plan, not replacing it.
- **Cancel the tool that is already running.** Rejected: finishing the step the model already asked for beats dropping a construction mid-apply. A time advance is the exception, because the player can end it and the clock restores.
