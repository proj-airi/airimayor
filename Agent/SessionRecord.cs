using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.AI;

namespace airimayor.Agent
{
    /// <summary>One trace-kit recording: native records and the complete evidence they reference.</summary>
    public sealed class SessionRecord : IDisposable
    {
        public const string RecordsFile = "records.jsonl";
        private const string EventsTrack = "city/events";
        private const string LogTrack = "city/diagnostics";
        private static readonly Regex s_SecretKey = new Regex("^(?:api[_ -]?key|authorization|x-api-key|(?:access[_ -]?|refresh[_ -]?)?token)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly object m_Lock = new object();
        private readonly Action<string> m_Warn;
        private readonly Func<SimulationStamp> m_Clock;
        private readonly Stopwatch m_Watch = Stopwatch.StartNew();
        private readonly AsyncLocal<Context> m_Context = new AsyncLocal<Context>();
        private readonly Dictionary<string, string> m_Calls = new Dictionary<string, string>();
        private readonly HashSet<string> m_Gaps = new HashSet<string>(StringComparer.Ordinal);
        private readonly JsonlFile m_Records;
        private long m_Sequence;
        private bool m_Incomplete;
        private bool m_Disposed;

        public SessionRecord(string sessionId, SessionRecordOptions options, Action<string> warn)
        {
            SessionId = sessionId;
            m_Warn = warn;
            m_Clock = options?.Clock;
            if (string.IsNullOrWhiteSpace(options?.Root)) return;
            try
            {
                Directory = Path.Combine(options.Root, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "--" + sessionId);
                global::System.IO.Directory.CreateDirectory(Directory);
                m_Records = new JsonlFile(Path.Combine(Directory, RecordsFile));
            }
            catch (Exception error) when (IsFileError(error)) { Directory = null; Fail(error); return; }

            EmitTrace(new JsonObject
            {
                ["type"] = "source.started", ["format"] = 1, ["runtime"] = "dotnet",
                ["profiles"] = new JsonArray(new JsonObject { ["key"] = "agent", ["version"] = "1" }),
            });
            using (var stream = typeof(SessionRecord).Assembly.GetManifestResourceStream("airimayor.Agent.TraceProfile.json"))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("The trace profile is missing.")))
                foreach (JsonObject definition in (JsonArray)JsonNode.Parse(reader.ReadToEnd())) EmitTrace(definition);
            DefineTrack(EventsTrack, "City evidence", 0);
            DefineTrack(LogTrack, "Diagnostics", 10);
            EmitTrace(new JsonObject
            {
                ["type"] = "definition", ["kind"] = "link", ["key"] = "linked",
                ["data"] = new JsonObject { ["rel"] = "linked", ["label"] = "Linked" },
            });
            EmitTrace(new JsonObject
            {
                ["type"] = "definition", ["kind"] = "id-key", ["key"] = "otel.span",
                ["data"] = new JsonObject { ["key"] = "otel.span", ["label"] = "External span" },
            });
            EmitTrace(new JsonObject
            {
                ["type"] = "clock.snapshot", ["uncertaintyMs"] = 1,
                ["values"] = new JsonArray(new JsonObject { ["clock"] = TraceClock, ["value"] = TraceNow }, new JsonObject { ["clock"] = "utc", ["value"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }),
            });
            if (options.Facts != null) Describe("session", options.Facts);
        }

        public string SessionId { get; }
        public string Directory { get; private set; }
        private string TraceClock => "mono:cs2:game:" + SessionId;
        internal double TraceNow => m_Watch.Elapsed.TotalMilliseconds;
        internal JsonObject TraceIds
        {
            get
            {
                var ids = (JsonObject)m_Context.Value?.Ids.DeepClone() ?? new JsonObject();
                ids["conversation"] = SessionId;
                return ids;
            }
        }

        public SimulationStamp Clock() => m_Clock?.Invoke();

        internal IDisposable BindRun(string agentSession, string run, string agent)
        {
            JsonObject ids = TraceIds;
            string previousSession = (string)ids["agentSession"];
            if (previousSession != null && previousSession != agentSession)
            {
                if (ids["turn"] != null) ids["originTurn"] = ids["turn"].DeepClone();
                if (ids["run"] != null) ids["originRun"] = ids["run"].DeepClone();
                ids.Remove("turn");
            }
            ids["agentSession"] = agentSession;
            ids["run"] = run;
            ids["agent"] = agent;
            return BindContext(ids);
        }

        internal IDisposable BindContext(JsonObject capturedIds)
        {
            Context previous = m_Context.Value;
            var ids = (JsonObject)capturedIds.DeepClone();
            ids["conversation"] = SessionId;
            string turn = (string)ids["turn"];
            m_Context.Value = new Context(ids, turn != null && turn == (string)previous?.Ids["turn"] ? previous?.TurnSpan : null);
            return new Binding(() => m_Context.Value = previous);
        }

        internal void EmitTrace(JsonObject input)
        {
            lock (m_Lock)
            {
                if (m_Records == null || m_Disposed) return;
                var record = (JsonObject)Redact(input);
                if (record.TryGetPropertyValue("attrs", out var attrs) && !(attrs is JsonObject))
                    throw new ArgumentException("Trace attributes must be an object.");
                ValidateAttributes(attrs as JsonObject);
                record["source"] = "cs2:game";
                record["epoch"] = SessionId;
                record["seq"] = m_Sequence++;
                if (record["ts"] == null) record["ts"] = TraceNow;
                if (record["clock"] == null) record["clock"] = TraceClock;
                var ids = (JsonObject)record["ids"]?.DeepClone() ?? TraceIds;
                ids["conversation"] = SessionId;
                record["ids"] = ids;
                try { m_Records.Append(record); }
                catch (Exception error) when (IsFileError(error)) { Fail(error); }
            }
        }

        internal JsonObject KeepAsset(JsonNode value)
        {
            if (Directory == null || m_Disposed) return null;
            return KeepAsset(Encoding.UTF8.GetBytes(value?.ToJsonString() ?? "null"), "application/json");
        }

        internal JsonObject KeepMessages(IEnumerable<ChatMessage> messages, int? tokenCount = null)
        {
            if (Directory == null || m_Disposed) return null;
            var snapshot = messages.ToList();
            var data = new JsonObject
            {
                ["messages"] = JsonSerializer.SerializeToNode(snapshot, AIJsonUtilities.DefaultOptions),
                ["messageIds"] = JsonSerializer.SerializeToNode(snapshot.Where(message => message.MessageId != null).Select(message => message.MessageId)),
                ["messageCount"] = snapshot.Count,
            };
            if (tokenCount.HasValue) data["tokenCount"] = tokenCount.Value;
            return KeepAsset(data);
        }

        internal static JsonObject ExceptionData(Exception error) => error == null ? null : new JsonObject
        {
            ["type"] = error.GetType().FullName, ["message"] = error.Message, ["stackTrace"] = error.StackTrace,
            ["inner"] = ExceptionData(error.InnerException),
        };

        public JsonObject KeepAsset(byte[] value, string mediaType)
        {
            lock (m_Lock)
            {
                if (Directory == null || m_Disposed) return null;
                byte[] bytes = value;
                if (mediaType == "application/json")
                    bytes = Encoding.UTF8.GetBytes(Redact(JsonNode.Parse(Encoding.UTF8.GetString(value)))?.ToJsonString() ?? "null");
                else if (mediaType.StartsWith("text/", StringComparison.Ordinal))
                    bytes = Encoding.UTF8.GetBytes(RedactSecrets(Encoding.UTF8.GetString(value)));
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                string relative = "assets/" + hash;
                try
                {
                    global::System.IO.Directory.CreateDirectory(Path.Combine(Directory, "assets"));
                    string path = Path.Combine(Directory, relative);
                    if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
                    return new JsonObject { ["$asset"] = relative, ["mediaType"] = mediaType, ["bytes"] = bytes.Length };
                }
                catch (Exception error) when (IsFileError(error)) { Fail(error); return null; }
            }
        }

        public void Describe(string key, JsonNode value) => Record("session.fact", new JsonObject { ["key"] = key, ["value"] = value?.DeepClone() });

        public void DeclareGap(string gap)
        {
            lock (m_Lock)
                if (m_Gaps.Add(gap)) Record("recording.gap", new JsonObject { ["message"] = gap });
        }

        public void Record(string type, JsonObject data, string call = null)
        {
            JsonObject ids = TraceIds;
            if (call != null) ids["toolCall"] = call;
            EmitTrace(new JsonObject { ["type"] = "instant", ["name"] = type, ["track"] = EventsTrack, ["ids"] = ids, ["attrs"] = DataAttributes(data) });
        }

        public void TurnStart(string turnId, string userText)
        {
            JsonObject ids = TraceIds;
            ids["turn"] = turnId;
            string span = Guid.NewGuid().ToString("N");
            m_Context.Value = new Context(ids, span);
            EmitTrace(new JsonObject
            {
                ["type"] = "span.begin", ["spanId"] = span, ["name"] = "chat-turn", ["track"] = "agents/mayor/turn",
                ["attrs"] = DataAttributes(new JsonObject { ["user"] = userText, ["simulation"] = Clock()?.ToJson() }),
            });
        }

        public void TurnFinish(JsonObject data)
        {
            Context context = m_Context.Value;
            if (context?.TurnSpan == null) return;
            string reason = (string)data?["stopReason"];
            EmitTrace(new JsonObject
            {
                ["type"] = "span.end", ["spanId"] = context.TurnSpan, ["track"] = "agents/mayor/turn",
                ["status"] = reason == "cancelled" ? "cancelled" : reason == "failed" ? "error" : "ok", ["attrs"] = DataAttributes(data),
            });
            var ids = (JsonObject)context.Ids.DeepClone();
            ids.Remove("turn");
            m_Context.Value = new Context(ids, null);
        }

        public void FunctionStart(string callId, string toolName)
        {
            if (NativeTool() != null) return;
            lock (m_Lock)
            {
                string span = Guid.NewGuid().ToString("N");
                m_Calls.Add(callId, span);
                EmitTrace(new JsonObject
                {
                    ["type"] = "span.begin", ["spanId"] = span, ["name"] = "execute_tool", ["track"] = ToolTrack,
                    ["ids"] = CallIds(callId), ["attrs"] = new JsonObject { ["gen_ai.tool.name"] = toolName },
                });
            }
        }

        public void Function(string callId, string toolName, string arguments, AgentToolResult result, long elapsedMs, ToolAnchors anchors, string error = null, string caller = null)
        {
            var data = new JsonObject
            {
                ["tool"] = toolName, ["arguments"] = arguments, ["result"] = result.Text, ["success"] = result.Success,
                ["elapsedMs"] = elapsedMs, ["error"] = error, ["caller"] = caller, ["evidence"] = result.Evidence?.DeepClone(),
            };
            if (anchors.QueuedMs.HasValue) data["queuedMs"] = anchors.QueuedMs.Value;
            var timeline = new JsonObject();
            if (anchors.Submitted != null) timeline["submitted"] = anchors.Submitted.ToJson();
            if (anchors.Applied != null) timeline["applied"] = anchors.Applied.ToJson();
            if (anchors.Completed != null) timeline["completed"] = anchors.Completed.ToJson();
            data["timeline"] = timeline;
            var files = new JsonArray();
            if (result.ImagePng != null) files.Add(new JsonObject { ["name"] = "image.png", ["asset"] = KeepAsset(result.ImagePng, "image/png") });
            if (result.Files != null)
                foreach (var file in result.Files) files.Add(new JsonObject { ["name"] = file.Key, ["asset"] = KeepAsset(file.Value, MediaType(file.Key)) });
            data["files"] = files;
            lock (m_Lock)
            {
                string span = HarnessTrace.SpanId(NativeTool());
                bool owned = span == null && m_Calls.TryGetValue(callId, out span);
                var instant = new JsonObject
                {
                    ["type"] = "instant", ["name"] = "city.tool.result", ["track"] = ToolTrack,
                    ["ids"] = CallIds(callId), ["attrs"] = DataAttributes(data),
                };
                if (span != null) instant["spanId"] = span;
                EmitTrace(instant);
                if (owned)
                {
                    EmitTrace(new JsonObject { ["type"] = "span.end", ["spanId"] = span, ["track"] = ToolTrack, ["status"] = result.Success ? "ok" : "error", ["ids"] = CallIds(callId) });
                    m_Calls.Remove(callId);
                }
            }
        }

        private JsonObject CallIds(string callId)
        {
            JsonObject ids = TraceIds;
            ids["toolCall"] = callId;
            return ids;
        }

        private string ToolTrack => "agents/" + ((string)TraceIds["agent"] ?? "mayor") + "/tools";
        private static Activity NativeTool()
        {
            Activity current = Activity.Current;
            return current?.Source.Name == "airimayor.harness" && (current.OperationName.StartsWith("execute_tool", StringComparison.Ordinal) || (string)current.GetTagItem("gen_ai.operation.name") == "execute_tool") ? current : null;
        }

        public void System(string source, string message) => Log(source, message, "info");
        public void Error(string source, string message) => Log(source, message, "error");
        public void Log(string source, string message, string level, Exception exception = null)
        {
            var attrs = new JsonObject { ["log.source"] = source };
            JsonObject evidence = exception == null ? null : KeepAsset(ExceptionData(exception));
            if (evidence != null) attrs["airimayor.exception"] = evidence;
            EmitTrace(new JsonObject { ["type"] = "log", ["track"] = LogTrack, ["level"] = level, ["message"] = message, ["attrs"] = attrs });
        }

        public static string Truncate(string value, int maxChars) => value == null || value.Length <= maxChars ? value : value.Substring(0, maxChars) + "…[truncated]";
        public static string RedactSecrets(string value) => JsonlFile.RedactSecrets(value);

        public void Dispose()
        {
            lock (m_Lock)
            {
                if (m_Disposed) return;
                if (!m_Incomplete) EmitTrace(new JsonObject { ["type"] = "source.stopped" });
                m_Disposed = true;
                try { m_Records?.Dispose(); }
                catch (Exception error) when (IsFileError(error)) { Fail(error); }
            }
        }

        private JsonObject DataAttributes(JsonNode data)
        {
            JsonObject asset = KeepAsset(data);
            return asset == null ? new JsonObject() : new JsonObject { ["airimayor.data"] = asset };
        }

        private void DefineTrack(string path, string label, int order) => EmitTrace(new JsonObject
        {
            ["type"] = "definition", ["kind"] = "track", ["key"] = path,
            ["data"] = new JsonObject { ["path"] = path, ["label"] = label, ["group"] = "city", ["order"] = order, ["render"] = new JsonArray("instants") },
        });

        private static JsonNode Redact(JsonNode value)
        {
            if (value is JsonObject obj)
            {
                var copy = new JsonObject();
                foreach (var item in obj) copy[item.Key] = s_SecretKey.IsMatch(item.Key) ? JsonValue.Create("[REDACTED]") : Redact(item.Value);
                return copy;
            }
            if (value is JsonArray array)
            {
                var copy = new JsonArray();
                foreach (var item in array) copy.Add(Redact(item));
                return copy;
            }
            return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? JsonValue.Create(RedactSecrets(text)) : value?.DeepClone();
        }

        private static void ValidateAttributes(JsonObject attrs)
        {
            if (attrs == null) return;
            foreach (var item in attrs)
            {
                JsonNode value = item.Value;
                if (value is JsonValue scalar && (scalar.GetValueKind() == JsonValueKind.String || scalar.GetValueKind() == JsonValueKind.Number || scalar.GetValueKind() == JsonValueKind.True || scalar.GetValueKind() == JsonValueKind.False)) continue;
                if (value is JsonObject asset && asset.Count == 3 && asset["$asset"]?.GetValueKind() == JsonValueKind.String && asset["mediaType"]?.GetValueKind() == JsonValueKind.String && asset["bytes"]?.GetValueKind() == JsonValueKind.Number) continue;
                if (value is JsonArray array)
                {
                    JsonValueKind? kind = null;
                    bool valid = true;
                    foreach (var element in array)
                    {
                        JsonValueKind? next = element?.GetValueKind();
                        if ((next != JsonValueKind.String && next != JsonValueKind.Number) || kind.HasValue && next != kind) { valid = false; break; }
                        kind = next;
                    }
                    if (valid) continue;
                }
                throw new ArgumentException("Trace attribute '" + item.Key + "' must be a primitive, homogeneous string/number array, or asset reference.");
            }
        }

        private static string MediaType(string name)
        {
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".json": return "application/json";
                case ".txt": case ".log": case ".csv": return "text/plain";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                default: return "application/octet-stream";
            }
        }

        private static bool IsFileError(Exception error) => error is IOException || error is UnauthorizedAccessException;
        private void Fail(Exception error)
        {
            m_Incomplete = true;
            m_Warn?.Invoke("session record incomplete: " + RedactSecrets(error.Message));
        }

        private sealed class Context
        {
            public Context(JsonObject ids, string turnSpan) { Ids = ids; TurnSpan = turnSpan; }
            public JsonObject Ids { get; }
            public string TurnSpan { get; }
        }

        private sealed class Binding : IDisposable
        {
            private readonly Action m_Restore;
            public Binding(Action restore) { m_Restore = restore; }
            public void Dispose() => m_Restore();
        }
    }
}
