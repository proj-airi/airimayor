# MAF Python summarization compared with .NET

Status: frozen

Checked 2026-11-10 at official main commit `098214127152b3bf26159c3e17ac0a019aa025b6`. Scope: Python `SummarizationStrategy`, source and existing mock tests; no provider execution or product change. This supplements the [harness comparison](./2026-11-10-harness-compaction-comparison.md) and [.NET investigation](./2026-11-10-native-summary-upstream-reports.md).

## Request shape

Python calls the summarizer with exactly two messages at the strategy boundary: a system message containing `self.prompt`, then one user message containing `_format_messages_for_summary(messages_to_summarize)`. It does not append the old messages with their original protocol roles, and it does not append a final summary instruction after the transcript. Client configuration and middleware can still affect the eventual provider request. [Send site](https://github.com/microsoft/agent-framework/blob/098214127152b3bf26159c3e17ac0a019aa025b6/python/packages/core/agent_framework/_compaction.py#L2420-L2430).

The formatter produces numbered text lines such as `1. [user] ...` and `2. [assistant] ...`. Tool calls include names, arguments and call IDs; results include result text and IDs. Those labels are text inside a user message, not actual provider conversation roles. This resembles Pi's treatment of history as source material, although Pi additionally places summary instructions after that material. [Formatter](https://github.com/microsoft/agent-framework/blob/098214127152b3bf26159c3e17ac0a019aa025b6/python/packages/core/agent_framework/_compaction.py#L2129-L2207), [tool-trajectory test](https://github.com/microsoft/agent-framework/blob/098214127152b3bf26159c3e17ac0a019aa025b6/python/packages/core/tests/core/test_compaction.py#L1815-L1841).

The default prompt asks for at most five sentences. The default summary input budget is 8,000 estimated tokens, independently configurable from the model's context window; it selects complete groups rather than blindly sending the full history. [Defaults and selection](https://github.com/microsoft/agent-framework/blob/098214127152b3bf26159c3e17ac0a019aa025b6/python/packages/core/agent_framework/_compaction.py#L2210-L2266).

## Failure and replacement

Provider exceptions or empty summary text log a warning and return false before excluding originals. After three consecutive failures an error is logged; that is an observability threshold, not three immediate retries. Nonempty text is accepted without semantic validation that it actually summarizes. Success inserts an assistant summary with provenance links and excludes selected original groups, leaving retained groups and system messages. [Implementation](https://github.com/microsoft/agent-framework/blob/098214127152b3bf26159c3e17ac0a019aa025b6/python/packages/core/agent_framework/_compaction.py#L2345-L2477). Tests explicitly assert that exceptions and empty responses do not exclude originals. [Failure tests](https://github.com/microsoft/agent-framework/blob/098214127152b3bf26159c3e17ac0a019aa025b6/python/packages/core/tests/core/test_compaction.py#L2230-L2322).

## Interpretation

The Python and .NET summary strategies are not equivalent. Python also leads with a system instruction, but it removes live conversation-role structure from the supplied history. It therefore should not inherit a claim of the exact .NET continuation failure without a separate experiment. Conversely, this framing alone does not prove DeepSeek compliance.

Cache inference: changing the system prompt and serializing original messages changes the token prefix, so this is not OpenCode's prefix-preserving summary request. Some common prefix may remain reusable depending on provider serialization; no cache-hit measurements were made. Replacing history with the resulting summary is a second prefix change. This note selects no implementation and does not establish Python/DeepSeek runtime behavior.
