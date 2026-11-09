# Native summaries are persisted context checkpoints

Status: accepted

Use MAF 1.27.0 `SummarizationCompactionStrategy` through the in-memory history provider's native chat reducer. Harness loads that provider before every service call, including iterations inside one tool loop. Persisting the summary there makes it the common history for subsequent calls. Disable the separate in-flight compaction provider: two independent summarizers can replace the same prefix with different summaries. This supersedes the tool-eviction/truncation policy and compaction-index invalidation in [0048](0048-native-maf-harness.md); Harness still owns the loop, history persistence, injection and background tasks.

Trigger at half of the estimated input budget (`window - output reserve`) and summarize old groups until the remaining estimated history reaches one fifth, subject to MAF's eight-group recent-history floor. The generated summary is then added. This gap leaves room for appended city work rather than rewriting a few old messages each round. Use the selected connection and native summary prompt, cap summary output at the smaller of 4,096 tokens and the selected output limit, and record summary requests and usage through the same native observation path. No handwritten summary loop or custom summary schema is introduced.

Keep instructions, tool definitions and the connection's conversation header stable. Between summary checkpoints, model history is append-only. Replacing a prefix at a checkpoint necessarily changes its cache identity; provider cache residency and hit percentages remain live evidence, not a local guarantee. MAF's byte-based history estimate is not a measurement of complete provider input, so live acceptance must inspect actual input usage as well as cache-read usage.

Retaining two summary entry points was rejected because their independent indexes do not share a generated summary. Using only in-flight summaries was rejected because retained history and cancellation recovery would still contain the unsummarized conversation. The native history reducer provides one persisted result without replacing Harness's execution machinery.
