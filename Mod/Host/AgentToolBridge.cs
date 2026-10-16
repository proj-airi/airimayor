using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        public JsonObject Evidence;     // city ledger before and after a write; session record only
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
        private const int LedgerTimeoutMs = 5_000;

        /// <param name="write">Read the city ledger before and after, as write evidence.</param>
        public static async Task<ToolInvocationResult> InvokeAsync(
            ToolDefinition tool,
            string argumentsJson,
            bool write,
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
            // Queued back to back so the simulation thread usually reads the
            // ledger in the same update that runs the write.
            Task<CS2MCP.BridgeResponse> before = write
                ? bridge.InvokeAsync(CS2MCP.RequestHandlers.LedgerRoute)
                : null;
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
            ToolInvocationResult result;
            if (!response.Success)
            {
                result = BridgeError(Encoding.UTF8.GetString(response.Body ?? Array.Empty<byte>()));
            }
            else if (string.Equals(tool.Response, "png", StringComparison.Ordinal))
            {
                result = new ToolInvocationResult
                {
                    Success = true,
                    ImagePng = response.Body,
                    PreviewBytes = response.Preview,
                    Files = response.Files,
                    Text = "{}",
                };
            }
            else
            {
                string text = Encoding.UTF8.GetString(response.Body ?? Array.Empty<byte>());
                if (advance)
                {
                    text = await CS2MCP.SimWaitService.WaitAsync(
                        bridge, text, cancellationToken);
                }
                result = new ToolInvocationResult
                {
                    Success = true,
                    Text = text,
                };
            }
            result.Applied = response.Applied;
            result.QueuedMs = response.QueuedMs;
            if (write)
            {
                result.Evidence = await LedgerEvidenceAsync(bridge, before);
            }
            return result;
        }

        /// <summary>
        /// The ledger before and after a write, for the session record only
        /// (ADR 0047). Null when either read fails or stalls; the record then
        /// shows no evidence rather than a guess.
        /// </summary>
        private static async Task<JsonObject> LedgerEvidenceAsync(
            CS2MCP.BridgeSystem bridge, Task<CS2MCP.BridgeResponse> before)
        {
            JsonObject start = await LedgerAsync(before);
            JsonObject end = await LedgerAsync(bridge.InvokeAsync(CS2MCP.RequestHandlers.LedgerRoute));
            if (start == null || end == null)
            {
                return null;
            }
            return new JsonObject { ["before"] = start, ["after"] = end };
        }

        private static async Task<JsonObject> LedgerAsync(Task<CS2MCP.BridgeResponse> read)
        {
            if (await Task.WhenAny(read, Task.Delay(LedgerTimeoutMs)) != read)
            {
                return null;
            }
            CS2MCP.BridgeResponse response = await read;
            return response.Success
                ? JsonNode.Parse(Encoding.UTF8.GetString(response.Body)) as JsonObject
                : null;
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
