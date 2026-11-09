using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace airimayor.Agent
{
    // Persist native summaries once so later requests append to the same history prefix.
    internal sealed class RecordedCompaction : CompactionStrategy
    {
        private readonly SummarizationCompactionStrategy m_Inner;
        private readonly SessionRecord m_Record;
        private readonly int m_WindowTokens;
        private readonly int m_OutputTokens;
        private readonly int m_TriggerTokens;
        private readonly int m_TargetTokens;

        public RecordedCompaction(IChatClient summarizer, int windowTokens, int outputTokens, SessionRecord record)
            : base(CompactionTriggers.TokensExceed((windowTokens - outputTokens) / 2))
        {
            m_Record = record;
            m_WindowTokens = windowTokens;
            m_OutputTokens = outputTokens;
            m_TriggerTokens = (windowTokens - outputTokens) / 2;
            m_TargetTokens = (windowTokens - outputTokens) / 5;
            m_Inner = new SummarizationCompactionStrategy(summarizer, CompactionTriggers.TokensExceed(m_TriggerTokens),
                target: index => index.IncludedTokenCount <= m_TargetTokens);
        }

        protected override async ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
        {
            if (m_Record.Directory == null)
                return await m_Inner.CompactAsync(index, logger, cancellationToken).ConfigureAwait(false);

            string operation = Guid.NewGuid().ToString("N");
            JsonObject before = m_Record.KeepMessages(index.GetIncludedMessages(), index.IncludedTokenCount);
            JsonObject details = new JsonObject
            {
                ["operationId"] = operation, ["phase"] = "history", ["strategy"] = nameof(SummarizationCompactionStrategy), ["reason"] = "context-window",
                ["maxContextWindowTokens"] = m_WindowTokens, ["maxOutputTokens"] = m_OutputTokens, ["inputBudgetTokens"] = m_WindowTokens - m_OutputTokens,
                ["triggerTokens"] = m_TriggerTokens, ["targetTokens"] = m_TargetTokens, ["minimumPreservedGroups"] = m_Inner.MinimumPreservedGroups, ["before"] = before,
            };
            m_Record.Record("compaction.started", details);
            bool changed = false;
            Exception failure = null;
            try
            {
                changed = await m_Inner.CompactAsync(index, logger, cancellationToken).ConfigureAwait(false);
                return changed;
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                details["after"] = m_Record.KeepMessages(index.GetIncludedMessages(), index.IncludedTokenCount);
                details["changed"] = changed;
                details["status"] = failure is OperationCanceledException ? "cancelled" : failure != null ? "error" : "ok";
                details["exception"] = SessionRecord.ExceptionData(failure);
                m_Record.Record("compaction.finished", details);
            }
        }
    }
}
