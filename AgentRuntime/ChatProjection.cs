using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AgentRuntime
{
    // Bounded, client-owned presentation state. It never supplies model history.
    internal sealed class ChatProjection
    {
        private readonly object m_Lock = new object();
        private readonly List<JsonObject> m_Rows = new List<JsonObject>();
        private string m_Plan = "";
        private AgentStatus m_Status;
        private JsonObject m_Assistant;
        public string SessionId { get; set; }

        public event Action<SessionUpdate> Updated;
        public AgentStatus Status
        {
            get
            {
                lock (m_Lock)
                    return m_Status;
            }
        }

        public void Apply(SessionUpdate update)
        {
            lock (m_Lock)
            {
                if (update.Kind == "status")
                    m_Status = update.Status;
                else if (update.Kind == "plan")
                    m_Plan = update.Text;
                else if (update.Kind == "delta")
                {
                    if (m_Assistant == null)
                    {
                        m_Assistant = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["text"] = ""
                        };
                        m_Rows.Add(m_Assistant);
                    }

                    m_Assistant["text"] = SessionRecord.Truncate((string)m_Assistant["text"] + update.Text, 4000);
                }
                else if (update.Kind == "tool")
                {
                    m_Assistant = null;
                    var row = m_Rows.LastOrDefault(item => (string)item["callId"] == update.CallId);
                    if (row == null)
                    {
                        row = new JsonObject
                        {
                            ["role"] = "tool",
                            ["callId"] = update.CallId
                        };
                        m_Rows.Add(row);
                    }

                    if (update.Tool != null)
                        row["tool"] = update.Tool;
                    row["status"] = update.Status.ToString();
                    if (update.Text != null)
                        row["arguments"] = update.Text;
                    if (update.Result != null)
                        row["text"] = SessionRecord.Truncate(update.Result, 4000);
                    if (update.Image != null)
                        row["image"] = update.Image;
                }
                else if (update.Kind == "user" || update.Kind == "error")
                {
                    m_Assistant = null;
                    m_Rows.Add(new JsonObject { ["role"] = update.Kind, ["text"] = SessionRecord.Truncate(update.Text, 4000), ["places"] = string.IsNullOrEmpty(update.Places) ? null : JsonNode.Parse(update.Places) });
                }
                else if (update.Kind == "turn")
                    m_Assistant = null;
                if (m_Rows.Count > 200)
                    m_Rows.RemoveRange(0, m_Rows.Count - 200);
            }

            Updated?.Invoke(update);
        }

        public string Json(int pendingInputs)
        {
            lock (m_Lock)
                return new JsonObject
                {
                    ["messages"] = new JsonArray(m_Rows.Select(row => row.DeepClone()).ToArray()),
                    ["plan"] = string.IsNullOrEmpty(m_Plan) ? null : JsonNode.Parse(m_Plan),
                    ["status"] = m_Status.ToString(),
                    ["session"] = SessionId,
                    ["pendingInputs"] = pendingInputs,
                    ["busy"] = m_Status == AgentStatus.Thinking || m_Status == AgentStatus.Working
                }.ToJsonString();
        }
    }
}
