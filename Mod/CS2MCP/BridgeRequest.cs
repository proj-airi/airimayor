using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using AgentRuntime;
using Newtonsoft.Json;

namespace CS2MCP
{
    /// <summary>
    /// One in-process tool command handed to the simulation thread.
    /// </summary>
    public sealed class BridgeRequest
    {
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body;

        /// <summary>Stopwatch timestamp when the request entered the queue.</summary>
        public long EnqueuedTimestamp;

        /// <summary>Clock where the simulation thread took the request; copied onto its response.</summary>
        public SimulationStamp Applied;

        /// <summary>Real time the request waited in the queue; copied onto its response.</summary>
        public long? QueuedMs;

        private readonly TaskCompletionSource<BridgeResponse> m_Completion =
            new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Async completion for in-process callers (agent loop).</summary>
        public Task<BridgeResponse> CompletionTask => m_Completion.Task;

        public bool TryGetInt(string key, out int value)
        {
            value = 0;
            return Query.TryGetValue(key, out string raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public bool TryGetFloat(string key, out float value)
        {
            value = 0f;
            return Query.TryGetValue(key, out string raw)
                && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public bool TryGetBool(string key, out bool value)
        {
            value = false;
            return Query.TryGetValue(key, out string raw) && bool.TryParse(raw, out value);
        }

        public void Complete(BridgeResponse response)
        {
            response.Applied = Applied;
            response.QueuedMs = QueuedMs;
            m_Completion.TrySetResult(response);
        }
    }

    public enum BridgeErrorKind
    {
        InvalidArguments,
        NotFound,
        Conflict,
        Unavailable,
        Timeout,
        Internal,
    }

    public sealed class BridgeResponse
    {
        public bool Success = true;
        public BridgeErrorKind? ErrorKind;
        public byte[] Body = Array.Empty<byte>();

        /// <summary>
        /// Optional UI-only thumbnail (JPEG) for image tools. Never reaches
        /// the model; the chat window renders it as the tool result preview.
        /// Best-effort: producers leave it null rather than failing the tool.
        /// </summary>
        public byte[] Preview;

        /// <summary>
        /// Optional diagnostic files the session record keeps beside the
        /// call. Never reach the model or the chat.
        /// </summary>
        public Dictionary<string, byte[]> Files;

        /// <summary>Clock where the simulation thread took the request.</summary>
        public SimulationStamp Applied;

        /// <summary>Real time the request waited in the queue.</summary>
        public long? QueuedMs;

        private static readonly JsonSerializerSettings s_JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static BridgeResponse Json(object payload)
        {
            return new BridgeResponse
            {
                Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload, s_JsonSettings)),
            };
        }

        public static BridgeResponse Text(string payload)
        {
            return new BridgeResponse
            {
                Body = Encoding.UTF8.GetBytes(payload ?? string.Empty),
            };
        }

        public static BridgeResponse Error(BridgeErrorKind kind, string message)
        {
            return new BridgeResponse
            {
                Success = false,
                ErrorKind = kind,
                Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
                    new { error = message, kind = ErrorKindName(kind) },
                    s_JsonSettings)),
            };
        }

        public static BridgeResponse Png(byte[] png, byte[] preview = null)
        {
            return new BridgeResponse { Body = png, Preview = preview };
        }

        private static string ErrorKindName(BridgeErrorKind kind)
        {
            switch (kind)
            {
                case BridgeErrorKind.InvalidArguments: return "invalid_arguments";
                case BridgeErrorKind.NotFound: return "not_found";
                case BridgeErrorKind.Conflict: return "conflict";
                case BridgeErrorKind.Unavailable: return "unavailable";
                case BridgeErrorKind.Timeout: return "timeout";
                default: return "internal";
            }
        }
    }
}
