using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRuntime;

namespace airimayor.Host
{
    /// <summary>Result of one in-process tool invocation.</summary>
    public sealed class ToolInvocationResult
    {
        public bool Success;
        public string Text;       // JSON or deterministic plain-text tool result
        public byte[] ImagePng;    // PNG bytes for the model, map or screenshot alike
        public byte[] PreviewBytes; // UI-only JPEG thumbnail for the chat window
        public Dictionary<string, byte[]> Files; // diagnostic files kept in the session record
        public SimulationStamp Applied; // clock where the simulation thread took the request
        public long? QueuedMs;          // real time the request waited for the simulation thread
    }

    /// <summary>
    /// Translates model tool calls into CS2MCP bridge requests and builds
    /// catalog query strings. Writes are not gated on pause. set_simulation
    /// advance blocks the agent thread until the timed run finishes; the
    /// city-side follow-up (wait mechanics only, no snapshot) lives in
    /// CS2MCP.SimWaitService so the loop stays free of city knowledge.
    /// </summary>
    public static class AgentToolBridge
    {
        private const int BridgeTimeoutMs = 90_000;

        public static async Task<ToolInvocationResult> InvokeAsync(
            ToolDefinition tool,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            CS2MCP.BridgeSystem bridge = CS2MCP.BridgeSystem.Instance;
            if (bridge == null)
            {
                return Error("bridge system not available (game still loading?)");
            }

            Dictionary<string, string> query;
            try
            {
                query = BuildQuery(tool, argumentsJson);
            }
            catch (Exception e)
            {
                return Error($"invalid arguments for {tool.Name}: {AgentRuntime.SessionRecord.RedactSecrets(e.Message)}");
            }

            bool advance = string.Equals(tool.Name, "set_simulation", StringComparison.Ordinal) &&
                query.TryGetValue("action", out string action) &&
                string.Equals(action, "advance", StringComparison.Ordinal);
            Task<CS2MCP.BridgeResponse> bridgeTask = bridge.InvokeAsync(tool.Route, query);
            Task completed = await Task.WhenAny(bridgeTask, Task.Delay(BridgeTimeoutMs, cancellationToken));
            CS2MCP.BridgeResponse response = completed == bridgeTask
                ? await bridgeTask
                : null;
            if (response == null)
            {
                if (advance)
                {
                    bridge.CancelTimedRun();
                }
                cancellationToken.ThrowIfCancellationRequested();
                return Error($"tool '{tool.Name}' did not complete within {BridgeTimeoutMs / 1000}s; " +
                             "the game may be busy, retry once or switch approach");
            }
            if (!response.Success)
            {
                string body = Encoding.UTF8.GetString(response.Body ?? Array.Empty<byte>());
                return Stamped(BridgeError(body), response);
            }
            if (string.Equals(tool.Response, "png", StringComparison.Ordinal))
            {
                return Stamped(new ToolInvocationResult
                {
                    Success = true,
                    ImagePng = response.Body,
                    PreviewBytes = response.Preview,
                    Files = response.Files,
                    Text = "{}",
                }, response);
            }

            string text = Encoding.UTF8.GetString(response.Body ?? Array.Empty<byte>());
            if (advance)
            {
                text = await CS2MCP.SimWaitService.WaitAsync(
                    bridge, text, cancellationToken);
            }
            return Stamped(new ToolInvocationResult
            {
                Success = true,
                Text = text,
            }, response);
        }

        private static ToolInvocationResult Stamped(ToolInvocationResult result, CS2MCP.BridgeResponse response)
        {
            result.Applied = response.Applied;
            result.QueuedMs = response.QueuedMs;
            return result;
        }

        private static ToolInvocationResult Error(string message)
        {
            string json = JsonSerializer.Serialize(new { error = message });
            return new ToolInvocationResult { Success = false, Text = json };
        }

        /// <summary>
        /// Bridge errors already use a JSON { error } envelope. Preserve that
        /// envelope instead of serializing the whole body as a second error
        /// string, which forces the model to decode nested JSON.
        /// </summary>
        private static ToolInvocationResult BridgeError(string body)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(body))
                    {
                        if (document.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            return new ToolInvocationResult { Success = false, Text = body };
                        }
                    }
                }
                catch (JsonException)
                {
                    // Fall through and give non-JSON bridge failures the normal
                    // local error envelope.
                }
            }
            return Error(string.IsNullOrWhiteSpace(body) ? "bridge request failed" : body);
        }

        private static Dictionary<string, string> BuildQuery(ToolDefinition tool, string argumentsJson)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            using (JsonDocument document = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(argumentsJson))
            {
                JsonElement args = document.RootElement;
                foreach (ToolQuerySpec spec in tool.Query)
                {
                    if (spec.Literal != null)
                    {
                        query[spec.Key] = spec.Literal;
                        continue;
                    }
                    if (spec.Arg == null)
                    {
                        continue;
                    }
                    if (!args.TryGetProperty(spec.Arg, out JsonElement value))
                    {
                        if (spec.Default != null)
                        {
                            query[spec.Key] = spec.Default;
                        }
                        continue;
                    }
                    if (spec.BoolMode == "trueOnly" && value.ValueKind == JsonValueKind.False)
                    {
                        continue;
                    }
                    query[spec.Key] = JsonValueToString(value);
                }
            }
            return query;
        }

        private static string JsonValueToString(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    return value.GetRawText();
            }
        }
    }
}
