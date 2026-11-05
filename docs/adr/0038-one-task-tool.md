# One task tool

Status: built-in delegation superseded by [0048](0048-native-maf-harness.md). The external mayor retains `task` and automatic report delivery.

The model opens an owned conversation with one tool, `task`, passing a `description` and a `prompt`. The call returns while that work is still running, and the final text is the root agent's next turn, including when the root agent is idle. The conversation does not follow the continuous setting. Stopping the current turn leaves it running. Closing the agent session stops it.

## Considered Options

- **Keep create, follow-up, list, status, history, and cancel.** Rejected: the model had to poll for a report that the host can deliver.
- **Name the tool `spawn_agent`.** Rejected: that name carries agent types and history forks this chat does not have.
- **Stopping the current turn cancels the conversation.** Rejected: the work outlives the turn that started it.
