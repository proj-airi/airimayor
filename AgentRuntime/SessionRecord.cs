using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentRuntime
{
    /// <summary>
    /// One session's record: a self-contained directory under the record
    /// root, named for its UTC start and session id. <c>session.json</c>
    /// holds the facts that occur once and gets <c>ended</c> last; a record
    /// without it was cut short. <c>timeline.jsonl</c> holds the
    /// task/turn/generation/function/compact/error events. Files a tool call
    /// returned live under <c>calls/&lt;call&gt;/</c>, and each owned
    /// conversation has its own record under <c>conversations/</c>.
    /// Every timeline line carries the same envelope: schema, seq, ts,
    /// session, turn, an optional call, and the simulation clock while a city
    /// runs. Events between turns carry no turn. API keys never appear.
    /// </summary>
    public sealed class SessionRecord : IDisposable
    {
        public const int Schema = 1;
        public const string SessionFile = "session.json";
        public const string TimelineFile = "timeline.jsonl";

        private readonly object m_Lock = new object();
        private readonly string m_SessionId;
        private readonly Action<string> m_Warn;
        private readonly Func<SimulationStamp> m_Clock;
        private readonly JsonObject m_Facts;
        private readonly JsonlFile m_Timeline;
        private string m_Turn;
        private long m_Sequence;
        private bool m_Disposed;

        public SessionRecord(string sessionId, SessionRecordOptions options, Action<string> warn)
        {
            m_SessionId = sessionId;
            m_Warn = warn;
            m_Clock = options?.Clock;
            DateTime started = DateTime.UtcNow;
            m_Facts = new JsonObject
            {
                ["schema"] = Schema,
                ["session"] = sessionId,
                ["parent"] = options?.Parent?.DeepClone(),
            };
            if (options?.Facts != null)
            {
                foreach (KeyValuePair<string, JsonNode> fact in options.Facts)
                {
                    m_Facts[fact.Key] = fact.Value?.DeepClone();
                }
            }
            m_Facts["started"] = Moment(started);
            m_Facts["knownGaps"] = new JsonArray();
            if (string.IsNullOrWhiteSpace(options?.Root))
            {
                return;
            }
            string directory = Path.Combine(
                options.Root,
                started.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "--" + sessionId);
            try
            {
                global::System.IO.Directory.CreateDirectory(directory);
                Directory = directory;
                WriteSessionFile();
                m_Timeline = new JsonlFile(
                    Path.Combine(directory, TimelineFile), warn, rotated => DeclareGap(rotated + " rotated out"));
            }
            catch (Exception e)
            {
                Directory = null;
                Warn("session record unavailable: " + e.Message);
            }
        }

        public string SessionId => m_SessionId;

        /// <summary>Record directory, or null when nothing is written.</summary>
        public string Directory { get; private set; }

        /// <summary>Latest simulation clock reading, or null while no city runs.</summary>
        public SimulationStamp Clock()
        {
            return m_Clock?.Invoke();
        }

        /// <summary>Sets one fact in <c>session.json</c>.</summary>
        public void Describe(string key, JsonNode value)
        {
            lock (m_Lock)
            {
                m_Facts[key] = value;
                WriteSessionFile();
            }
        }

        /// <summary>
        /// Names something this record cannot show, once. A gap is declared,
        /// never filled in: unknown is not written as empty.
        /// </summary>
        public void DeclareGap(string gap)
        {
            lock (m_Lock)
            {
                var gaps = (JsonArray)m_Facts["knownGaps"];
                foreach (JsonNode known in gaps)
                {
                    if ((string)known == gap)
                    {
                        return;
                    }
                }
                gaps.Add(gap);
                WriteSessionFile();
            }
        }

        /// <summary>Record options for a conversation this session opens in its current turn.</summary>
        public SessionRecordOptions Conversation(string conversationId)
        {
            lock (m_Lock)
            {
                var facts = new JsonObject();
                foreach (string key in new[] { "head", "modVersion", "city" })
                {
                    if (m_Facts.TryGetPropertyValue(key, out JsonNode value))
                    {
                        facts[key] = value?.DeepClone();
                    }
                }
                return new SessionRecordOptions
                {
                    Root = Directory == null ? null : Path.Combine(Directory, "conversations"),
                    Clock = m_Clock,
                    Facts = facts,
                    Parent = new JsonObject
                    {
                        ["session"] = m_SessionId,
                        ["turn"] = m_Turn,
                        ["conversation"] = conversationId,
                    },
                };
            }
        }

        /// <summary>
        /// Keeps what a tool call returned beside its events: the model image
        /// and any diagnostic files. Returns the record-relative paths.
        /// </summary>
        public JsonArray SaveCallFiles(string callId, byte[] image, IReadOnlyDictionary<string, byte[]> files)
        {
            var saved = new JsonArray();
            bool hasImage = image != null && image.Length > 0;
            if (Directory == null || (!hasImage && (files == null || files.Count == 0)))
            {
                return saved;
            }
            string relative = "calls/" + FileSafe(callId);
            try
            {
                string directory = Path.Combine(Directory, "calls", FileSafe(callId));
                global::System.IO.Directory.CreateDirectory(directory);
                if (hasImage)
                {
                    File.WriteAllBytes(Path.Combine(directory, "image.png"), image);
                    saved.Add(relative + "/image.png");
                }
                if (files != null)
                {
                    foreach (KeyValuePair<string, byte[]> file in files)
                    {
                        File.WriteAllBytes(Path.Combine(directory, FileSafe(file.Key)), file.Value);
                        saved.Add(relative + "/" + FileSafe(file.Key));
                    }
                }
            }
            catch (Exception e)
            {
                Warn("session record could not keep call files: " + e.Message);
            }
            return saved;
        }

        public void Record(string type, JsonObject data, string call = null)
        {
            lock (m_Lock)
            {
                if (m_Timeline == null || m_Disposed)
                {
                    return;
                }
                var line = new JsonObject
                {
                    ["schema"] = Schema,
                    ["seq"] = ++m_Sequence,
                    ["ts"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["session"] = m_SessionId,
                    ["turn"] = m_Turn,
                };
                if (call != null)
                {
                    line["call"] = call;
                }
                SimulationStamp clock = Clock();
                if (clock != null)
                {
                    line["clock"] = clock.ToJson();
                }
                line["type"] = type;
                line["data"] = data ?? new JsonObject();
                m_Timeline.Append(line);
            }
        }

        public void TaskStart(string model, long windowTokens, double compactThreshold, string apiKind)
        {
            Record("task.start", new JsonObject
            {
                ["model"] = model,
                ["windowTokens"] = windowTokens,
                ["compactThreshold"] = compactThreshold,
                ["apiKind"] = apiKind,
            });
        }

        public void TurnStart(string turnId, string userText)
        {
            lock (m_Lock)
            {
                m_Turn = turnId;
            }
            Record("turn.start", new JsonObject { ["user"] = userText });
        }

        public void TurnFinish(int generationCount, int functionCount, long elapsedMs, JsonObject usage, JsonObject coverage)
        {
            Record("turn.finish", new JsonObject
            {
                ["generations"] = generationCount,
                ["functions"] = functionCount,
                ["elapsedMs"] = elapsedMs,
                ["usage"] = usage ?? new JsonObject(),
                ["coverage"] = coverage ?? new JsonObject(),
            });
            lock (m_Lock)
            {
                m_Turn = null;
            }
        }

        public void Generation(string model, string messageSummary, string reasoning, JsonArray toolCalls, JsonObject usage, long elapsedMs)
        {
            Record("generation", new JsonObject
            {
                ["model"] = model,
                ["input"] = Truncate(messageSummary, 32768),
                ["reasoning"] = Truncate(reasoning, 32768),
                ["toolCalls"] = toolCalls ?? new JsonArray(),
                ["usage"] = usage ?? new JsonObject(),
                ["elapsedMs"] = elapsedMs,
            });
        }

        /// <summary>Written before a tool runs, so a call that never ends still leaves a trace.</summary>
        public void FunctionStart(string callId, string toolName)
        {
            Record("function.start", new JsonObject { ["tool"] = toolName }, callId);
        }

        /// <summary>
        /// One finished tool call. Submitted and completed come from the
        /// clock; applied is the frame where the simulation thread ran the
        /// request, absent for tools that never reach it. Returned images and
        /// diagnostic files are kept under the call and listed in <c>files</c>.
        /// </summary>
        public void Function(
            string callId,
            string toolName,
            string arguments,
            AgentToolResult result,
            long elapsedMs,
            ToolAnchors anchors,
            string error = null)
        {
            var data = new JsonObject
            {
                ["tool"] = toolName,
                ["arguments"] = Truncate(arguments, 32768),
                ["result"] = Truncate(result.Text, 32768),
                ["success"] = result.Success,
                ["elapsedMs"] = elapsedMs,
                ["error"] = error,
            };
            if (anchors.QueuedMs.HasValue)
            {
                data["queuedMs"] = anchors.QueuedMs.Value;
            }
            var timeline = new JsonObject();
            AddAnchor(timeline, "submitted", anchors.Submitted);
            AddAnchor(timeline, "applied", anchors.Applied);
            AddAnchor(timeline, "completed", anchors.Completed);
            data["timeline"] = timeline;
            JsonArray files = SaveCallFiles(callId, result.ImagePng, result.Files);
            if (files.Count > 0)
            {
                data["files"] = files;
            }
            Record("function", data, callId);
        }

        public void Compact(double threshold, int removedMessages, int keptMessages, string summary, long newEstimate)
        {
            Record("compact", new JsonObject
            {
                ["threshold"] = threshold,
                ["removedMessages"] = removedMessages,
                ["keptMessages"] = keptMessages,
                ["summary"] = Truncate(summary, 32768),
                ["newEstimate"] = newEstimate,
            });
        }

        public void System(string source, string message)
        {
            Record("system", new JsonObject
            {
                ["source"] = source,
                ["message"] = Truncate(message, 32768),
            });
        }

        public void Error(string source, string message)
        {
            Record("error", new JsonObject
            {
                ["source"] = source,
                ["message"] = Truncate(message, 8192),
            });
        }

        public static string Truncate(string value, int maxChars)
        {
            if (value == null)
            {
                return null;
            }
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "…[truncated]";
        }

        public static string RedactSecrets(string value)
        {
            return JsonlFile.RedactSecrets(value);
        }

        public void Dispose()
        {
            lock (m_Lock)
            {
                if (m_Disposed)
                {
                    return;
                }
                m_Disposed = true;
                m_Timeline?.Dispose();
                m_Facts["ended"] = Moment(DateTime.UtcNow);
                WriteSessionFile();
            }
        }

        private JsonObject Moment(DateTime utc)
        {
            var moment = new JsonObject { ["utc"] = utc.ToString("o", CultureInfo.InvariantCulture) };
            SimulationStamp clock = Clock();
            if (clock != null)
            {
                moment["clock"] = clock.ToJson();
            }
            return moment;
        }

        private static void AddAnchor(JsonObject timeline, string name, SimulationStamp stamp)
        {
            if (stamp != null)
            {
                timeline[name] = stamp.ToJson();
            }
        }

        /// <summary>Writes beside the file and swaps it in, so a reader never sees half a document.</summary>
        private void WriteSessionFile()
        {
            if (Directory == null)
            {
                return;
            }
            string path = Path.Combine(Directory, SessionFile);
            string staging = path + ".tmp";
            try
            {
                File.WriteAllText(staging, m_Facts.ToJsonString(), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(staging, path, null);
                }
                else
                {
                    File.Move(staging, path);
                }
            }
            catch (Exception e)
            {
                Warn("session record could not write " + SessionFile + ": " + e.Message);
            }
        }

        private static string FileSafe(string name)
        {
            var builder = new StringBuilder(name ?? "");
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                builder.Replace(invalid, '_');
            }
            return builder.Length == 0 ? "_" : builder.ToString();
        }

        private void Warn(string message)
        {
            m_Warn?.Invoke(message);
        }
    }
}
