using System.Collections.Generic;

namespace airimayor.Agent
{
    /// <summary>
    /// Maps one external-agent update onto the chat stream. ACP tool rows
    /// merge by call id.
    /// </summary>
    public static class AcpChatMap
    {
        public static SessionUpdate AgentText(string text)
        {
            return new SessionUpdate { Kind = "delta", Text = text ?? "" };
        }

        public static SessionUpdate ToolRow(AcpToolState state)
        {
            return new SessionUpdate
            {
                Kind = "tool",
                CallId = state.CallId,
                Tool = state.Title,
                Text = state.RawInput ?? "",
                Result = state.Content,
                Output = state.RawOutput,
                Status = !state.Finished
                    ? AgentStatus.Working
                    : state.Failed ? AgentStatus.Error : AgentStatus.Idle,
            };
        }

        public static SessionUpdate Plan(string entriesJson)
        {
            return new SessionUpdate { Kind = "plan", Text = entriesJson ?? "" };
        }

        public static string PlanJson(IEnumerable<PlanStep> entries)
        {
            var steps = new List<(string Content, string Priority, string Status)>();
            if (entries != null)
            {
                foreach (PlanStep entry in entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Content))
                    {
                        continue;
                    }
                    steps.Add((entry.Content, NormalizePriority(entry.Priority), NormalizeStatus(entry.Status)));
                }
            }
            return SessionPlan.Format(steps);
        }

        private static string NormalizePriority(string priority)
        {
            if (priority == "high" || priority == "low")
            {
                return priority;
            }
            return "medium";
        }

        private static string NormalizeStatus(string status)
        {
            if (status == "in_progress" || status == "completed")
            {
                return status;
            }
            return "pending";
        }
    }

    public sealed class PlanStep
    {
        public string Content;
        public string Priority;
        public string Status;
    }

    /// <summary>
    /// One ACP tool call after merging updates. A null field has not arrived.
    /// </summary>
    public sealed class AcpToolState
    {
        public string CallId;
        public string Title;
        public string RawInput;
        public string Content;
        public string RawOutput;
        public bool Finished;
        public bool Failed;

        public static AcpToolState Apply(AcpToolState current, string callId, AcpToolPatch patch)
        {
            var next = current ?? new AcpToolState { CallId = callId };
            next.CallId = callId;
            if (patch == null)
            {
                return next;
            }
            if (patch.HasTitle)
            {
                next.Title = patch.Title;
            }
            if (patch.HasRawInput)
            {
                next.RawInput = patch.RawInput;
            }
            if (patch.HasContent)
            {
                next.Content = patch.Content;
            }
            if (patch.HasRawOutput)
            {
                next.RawOutput = patch.RawOutput;
            }
            if (patch.HasStatus)
            {
                next.Finished = patch.Finished;
                next.Failed = patch.Failed;
            }
            return next;
        }
    }

    /// <summary>
    /// Fields present on one tool_call or tool_call_update. A false flag means
    /// the agent omitted that field.
    /// </summary>
    public sealed class AcpToolPatch
    {
        public bool HasTitle;
        public string Title;
        public bool HasRawInput;
        public string RawInput;
        public bool HasContent;
        public string Content;
        public bool HasRawOutput;
        public string RawOutput;
        public bool HasStatus;
        public bool Finished;
        public bool Failed;
    }
}
