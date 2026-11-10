# Native summary failures: upstream reports

Status: frozen

Checked: 2026-11-10. Scope: focused public issue, pull-request, source and test review. Local comparison: [diagnosis](../ops/2026-11-10-native-summary-diagnosis.md) and [ordering controls](../ops/2026-11-10-native-summary-order-controls.md). No upstream reports were posted and no product changes were made.

## Microsoft Agent Framework

The search found related compaction failures, but no public report establishing the exact .NET failure reproduced locally: a valid summarization request instead continues historical work, and moving the unchanged system instruction to the end changes the task performed. This is a search result, not proof that nobody has discovered or reported it.

Current .NET source still creates the summary system message first and appends the selected historical messages. It accepts nonblank response text without testing whether it summarizes, substitutes `[Summary unavailable]` for whitespace, inserts the replacement summary, and returns success. The inspected current file matches [commit `03a96faf`, lines 148–222](https://github.com/microsoft/agent-framework/blob/03a96faf431791a8f1f6707f2f2d2638bf886bd2/dotnet/src/Microsoft.Agents.AI/Compaction/SummarizationCompactionStrategy.cs#L148-L222).

This fallback is expressly covered by a mock-client test: whitespace output is expected to become `[Summary unavailable]`. Another test expects the custom summary instruction as the first system message, followed by the original user message. These tests establish the implementation contract; they do not establish model compliance. See [empty-response test](https://github.com/microsoft/agent-framework/blob/098214127152b3bf26159c3e17ac0a019aa025b6/dotnet/tests/Microsoft.Agents.AI.UnitTests/Compaction/SummarizationCompactionStrategyTests.cs#L143-L163) and [custom-prompt test](https://github.com/microsoft/agent-framework/blob/098214127152b3bf26159c3e17ac0a019aa025b6/dotnet/tests/Microsoft.Agents.AI.UnitTests/Compaction/SummarizationCompactionStrategyTests.cs#L188-L223).

The file's returned history contains three commits: introduction in [PR #4533](https://github.com/microsoft/agent-framework/pull/4533), merged March 11; logging-warning cleanup in [#4634](https://github.com/microsoft/agent-framework/pull/4634); and security documentation in [#6933](https://github.com/microsoft/agent-framework/pull/6933), merged July 7. No ordering or summary-content validation fix appears in that history. The security documentation explicitly recognizes that summarizer output permanently replaces history; that is not a report of this particular failure.

| Related report | Verified public status | Difference from local failure |
| --- | --- | --- |
| [Python #7214](https://github.com/microsoft/agent-framework/issues/7214), July 20 | Closed completed; [#7375](https://github.com/microsoft/agent-framework/pull/7375) merged July 30 | Oversized summary input causes provider rejection and skipped compaction. |
| [Python #8086](https://github.com/microsoft/agent-framework/issues/8086), September 5 | Closed completed; [#8087](https://github.com/microsoft/agent-framework/pull/8087) merged September 8 | Transcript formatting omits tool names, arguments and results. |
| [.NET #4924](https://github.com/microsoft/agent-framework/issues/4924), March 26 | Closed completed April 8 | History duplication occurs even when summarization never runs. |

## Closely related OpenCode report

Yes: [OpenCode #42363](https://github.com/anomalyco/opencode/issues/42363), filed August 13, reports DeepSeek V4 Flash replaying reasoning/tool history instead of summarizing. Fresh GitHub API verification found it closed October 11 as `not_planned`; the maintainer explains this was bulk closure of V1 issues after V2 launched, not confirmation of a fix.

An August 19 comment on [#42363](https://github.com/anomalyco/opencode/issues/42363), with a separate source-level report in [#43247](https://github.com/anomalyco/opencode/issues/43247), identifies an intended system prompt that never reaches the actual send site. That explanation does not transfer unchanged here: the local MAF request contains the summary instruction, removing reasoning still fails, and even two messages reproduce the semantic failure. Moving the exact system instruction last produces summaries in both full-history controls. The external symptom closely matches; an identical cause is not established. OpenCode's own compaction implementation is separate from this project's MAF client calling the OpenCode Go model endpoint.

## Search limits

GitHub API issue and PR queries covered `SummarizationCompactionStrategy`, compaction with `summary unavailable`, summarization with `prompt` and `order`, and summarizer with `continue`; broader compaction queries and the introducing PR discussion were also inspected. Statuses above came from fetched issue/PR objects, not search snippets. This was a focused review, not an exhaustive search of every discussion, private report or provider implementation. Related reports do not establish the local model-versus-gateway cause or a reliable production remedy.

## Follow-up: MAF instruction-order reports

A further targeted search on November 10 found two closer MAF reports:

- [Python #8352](https://github.com/microsoft/agent-framework/issues/8352), filed September 13: partial deduplication in `prepend_instructions_to_messages` reverses configured instructions from `[First, Second]` to `[Second, First]`. [Fix #8353](https://github.com/microsoft/agent-framework/pull/8353) merged September 15; [#8360](https://github.com/microsoft/agent-framework/pull/8360#issuecomment-5659317206) was closed unmerged as its duplicate. This is an actual instruction-order defect, but in Python instruction deduplication, not the .NET summary instruction's position relative to historical messages.
- [Python #3507](https://github.com/microsoft/agent-framework/issues/3507), filed January 29 and closed completed February 1: a reviewer writes another tagline, and a summarizer/classifier workflow answers the original task instead of following the assigned role. A [maintainer reproduced the problem specifically with `AzureAIAgentClient`](https://github.com/microsoft/agent-framework/issues/3507#issuecomment-3826113402). It is a similar task-selection failure in workflow agents, not a controlled compaction-request ordering reproduction.

The follow-up searched open and closed issues and PRs separately for summarization, summarizer, DeepSeek, reducer/system, compaction/order, summarize/system, instruction, role, continue and history; selected searches included comments. Semantic Kernel results concerned tool-pair chronology and saved-summary retention, not this instruction-placement failure. No exact matching .NET compaction issue or merged fix was located. The absence of a matching search result does not establish that nobody has reported it.
