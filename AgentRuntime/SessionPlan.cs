using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace AgentRuntime
{
    internal static class SessionPlan
    {
        internal static string Format(IEnumerable<(string Content, string Priority, string Status)> steps)
        {
            var entries = new JsonArray();
            foreach ((string content, string priority, string status)in steps)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                entries.Add(new JsonObject { ["content"] = content, ["priority"] = priority, ["status"] = status, });
            }

            if (entries.Count == 0)
            {
                return "";
            }

            return new JsonObject
            {
                ["entries"] = entries,
            }.ToJsonString();
        }
    }
}