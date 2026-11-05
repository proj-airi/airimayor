using System.Text.Json.Nodes;

namespace airimayor.Agent
{
    public enum AgentStatus
    {
        Idle,
        Thinking,
        Working,
        Interrupted,
        Error,
    }

    /// <summary>Client-facing chat event.</summary>
    public sealed class SessionUpdate
    {
        public string Kind; // status|delta|tool|user|error|turn|plan
        public string Text;
        public string Tool;
        public AgentStatus Status;
        /// <summary>Stable tool call id for patching a row.</summary>
        public string CallId;
        /// <summary>Tool result text, when available.</summary>
        public string Result;
        /// <summary>Raw tool output JSON, when available.</summary>
        public string Output;
        /// <summary>UI-only image preview (data URI) for image tool results.</summary>
        public string Image;
        /// <summary>JSON array of pointed places on a player message. Absent otherwise.</summary>
        public string Places;
        public string ToJsonString()
        {
            var obj = new JsonObject
            {
                ["kind"] = Kind ?? "",
                ["text"] = Text ?? "",
                ["status"] = Status.ToString(),
            };
            if (Tool != null)
            {
                obj["tool"] = Tool;
            }

            if (CallId != null)
            {
                obj["callId"] = CallId;
            }

            if (Result != null)
            {
                obj["result"] = Result;
            }

            if (Output != null)
            {
                obj["output"] = Output;
            }

            if (Image != null)
            {
                obj["image"] = Image;
            }

            if (!string.IsNullOrEmpty(Places))
            {
                JsonNode places = JsonNode.Parse(Places);
                if (places != null)
                {
                    obj["places"] = places;
                }
            }

            return obj.ToJsonString();
        }
    }
}
