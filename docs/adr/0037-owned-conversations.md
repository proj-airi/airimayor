# Both mayors coordinate owned conversations

Status: built-in delegation superseded by [0048](0048-native-maf-harness.md). The external mayor retains owned conversations and the one-task interface in [0038](0038-one-task-tool.md).

The player has one chat. `Prompt` reaches only that root conversation. Both the built-in mayor and an external mayor can open other conversations. The player cannot address those conversations. How the model opens one, and how the report comes back, is [0038](./0038-one-task-tool.md). An external child is `session/new` plus `session/prompt` on the same connection, so a permission ask for it is answered: city tools are allowed, anything else is cancelled. OpenCode's built-in `task` tool stays denied because that tool opens a conversation this client does not own.

## Considered Options

- **Let the player message the other conversation from the composer.** Rejected: the player talks to the root mayor only.
- **Leave OpenCode's `task` tool enabled.** Rejected: that conversation is not created with `session/new`, so a permission ask never arrives and the turn never ends.
