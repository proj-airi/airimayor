using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace airimayor.Agent
{
    /// <summary>
    /// UI-only preview carrier. Base64 over the event binding is pure .NET
    /// (safe on the agent thread); the thumbnail itself was rendered on the
    /// main thread at capture time. Oversized payloads stay text-only.
    /// </summary>
    public static class ToolPreview
    {
        private const int MaxBytes = 256 * 1024;

        public static string DataUri(byte[] preview)
        {
            if (preview == null || preview.Length == 0 || preview.Length > MaxBytes)
            {
                return null;
            }
            return "data:image/jpeg;base64," + Convert.ToBase64String(preview);
        }
    }

    /// <summary>
    /// Pairs an in-game JPEG preview with an ACP tool call. Either side may
    /// arrive first. One preview attaches to one call.
    /// </summary>
    public sealed class ToolPreviewLatch
    {
        private readonly object m_Gate = new object();
        private readonly Action<string, string> m_Matched;
        private readonly List<PendingCall> m_Calls = new List<PendingCall>();
        private readonly List<PendingPreview> m_Previews = new List<PendingPreview>();
        private readonly HashSet<string> m_Attached = new HashSet<string>(StringComparer.Ordinal);

        public ToolPreviewLatch(Action<string, string> matched)
        {
            m_Matched = matched;
        }

        public void Offer(string toolName, string argumentsJson, byte[] jpeg)
        {
            string uri = ToolPreview.DataUri(jpeg);
            if (uri == null)
            {
                return;
            }
            string name = toolName ?? "";
            string arguments = CanonicalArguments(argumentsJson);
            string callId = null;
            lock (m_Gate)
            {
                int index = FindCall(name, arguments);
                if (index >= 0)
                {
                    callId = m_Calls[index].CallId;
                    m_Calls.RemoveAt(index);
                    m_Attached.Add(callId);
                }
                else
                {
                    m_Previews.Add(new PendingPreview
                    {
                        Name = name,
                        Arguments = arguments,
                        Uri = uri,
                    });
                }
            }
            if (callId != null)
            {
                m_Matched?.Invoke(callId, uri);
            }
        }

        public string Claim(string callId, string title, string rawInput)
        {
            if (string.IsNullOrEmpty(callId))
            {
                return null;
            }
            string name = ToolName(title);
            string arguments = rawInput == null ? null : CanonicalArguments(rawInput);
            lock (m_Gate)
            {
                if (m_Attached.Contains(callId))
                {
                    return null;
                }
                int index = IndexOfCall(callId);
                PendingCall call = index >= 0 ? m_Calls[index] : new PendingCall { CallId = callId };
                call.Name = name;
                if (arguments != null)
                {
                    call.Arguments = arguments;
                }
                int preview = FindPreview(call.Name, call.Arguments);
                if (preview >= 0)
                {
                    string uri = m_Previews[preview].Uri;
                    m_Previews.RemoveAt(preview);
                    if (index >= 0)
                    {
                        m_Calls.RemoveAt(index);
                    }
                    m_Attached.Add(callId);
                    return uri;
                }
                if (index < 0)
                {
                    m_Calls.Add(call);
                }
                return null;
            }
        }

        private static string ToolName(string title)
        {
            if (title != null && title.StartsWith("city_", StringComparison.Ordinal))
            {
                return title.Substring("city_".Length);
            }
            return title ?? "";
        }

        private int IndexOfCall(string callId)
        {
            for (int i = 0; i < m_Calls.Count; i++)
            {
                if (string.Equals(m_Calls[i].CallId, callId, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        private int FindCall(string name, string arguments)
        {
            int exact = -1;
            int unnamed = -1;
            int unnamedCount = 0;
            int namedCount = 0;
            for (int i = 0; i < m_Calls.Count; i++)
            {
                PendingCall call = m_Calls[i];
                if (!string.Equals(call.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }
                if (call.Arguments != null && call.Arguments == arguments)
                {
                    namedCount++;
                    if (exact < 0)
                    {
                        exact = i;
                    }
                }
                else if (call.Arguments == null)
                {
                    unnamedCount++;
                    if (unnamed < 0)
                    {
                        unnamed = i;
                    }
                }
                else
                {
                    namedCount++;
                }
            }
            if (exact >= 0)
            {
                return exact;
            }
            if (unnamedCount == 1 && namedCount == 0)
            {
                return unnamed;
            }
            return -1;
        }

        private int FindPreview(string name, string arguments)
        {
            int exact = -1;
            int count = 0;
            int first = -1;
            for (int i = 0; i < m_Previews.Count; i++)
            {
                PendingPreview preview = m_Previews[i];
                if (!string.Equals(preview.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }
                count++;
                if (first < 0)
                {
                    first = i;
                }
                if (arguments != null && preview.Arguments == arguments && exact < 0)
                {
                    exact = i;
                }
            }
            if (exact >= 0)
            {
                return exact;
            }
            if (arguments == null && count == 1)
            {
                return first;
            }
            return -1;
        }

        private static string CanonicalArguments(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return "";
            }
            try
            {
                JsonNode node = JsonNode.Parse(json);
                return CanonicalNode(node);
            }
            catch (JsonException)
            {
                return json.Trim();
            }
        }

        private static string CanonicalNode(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                var names = new List<string>();
                foreach (KeyValuePair<string, JsonNode> pair in obj)
                {
                    names.Add(pair.Key);
                }
                names.Sort(StringComparer.Ordinal);
                var sorted = new JsonObject();
                foreach (string name in names)
                {
                    sorted[name] = CopyNode(obj[name]);
                }
                return sorted.ToJsonString();
            }
            if (node is JsonArray array)
            {
                var copy = new JsonArray();
                foreach (JsonNode item in array)
                {
                    copy.Add(CopyNode(item));
                }
                return copy.ToJsonString();
            }
            return node == null ? "null" : node.ToJsonString();
        }

        private static JsonNode CopyNode(JsonNode node)
        {
            if (node == null)
            {
                return null;
            }
            return JsonNode.Parse(CanonicalNode(node));
        }

        private sealed class PendingCall
        {
            public string CallId;
            public string Name;
            public string Arguments;
        }

        private sealed class PendingPreview
        {
            public string Name;
            public string Arguments;
            public string Uri;
        }
    }
}
