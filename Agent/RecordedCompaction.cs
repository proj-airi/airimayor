using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.Logging;

namespace airimayor.Agent
{
    // Observe the SDK strategy at the same seam for in-loop and stored-history reduction.
    internal sealed class RecordedCompaction : CompactionStrategy
    {
        private readonly ContextWindowCompactionStrategy m_Inner;
        private readonly SessionRecord m_Record;
        private readonly string m_Phase;

        public RecordedCompaction(ContextWindowCompactionStrategy inner, SessionRecord record, string phase) : base(_ => true)
        {
            m_Inner = inner;
            m_Record = record;
            m_Phase = phase;
        }

        protected override async ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
        {
            if (m_Record.Directory == null || index.IncludedTokenCount <= (int)(m_Inner.InputBudgetTokens * m_Inner.ToolEvictionThreshold))
                return await m_Inner.CompactAsync(index, logger, cancellationToken).ConfigureAwait(false);

            string operation = Guid.NewGuid().ToString("N");
            JsonObject before = m_Record.KeepMessages(index.GetIncludedMessages(), index.IncludedTokenCount);
            JsonObject details = new JsonObject
            {
                ["operationId"] = operation, ["phase"] = m_Phase, ["strategy"] = nameof(ContextWindowCompactionStrategy), ["reason"] = "context-window",
                ["maxContextWindowTokens"] = m_Inner.MaxContextWindowTokens, ["maxOutputTokens"] = m_Inner.MaxOutputTokens, ["inputBudgetTokens"] = m_Inner.InputBudgetTokens,
                ["toolEvictionThreshold"] = m_Inner.ToolEvictionThreshold, ["truncationThreshold"] = m_Inner.TruncationThreshold, ["before"] = before,
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
