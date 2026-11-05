using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace airimayor.Agent
{
    /// <summary>
    /// Session facts, city-tool evidence and host events. Each owned ACP
    /// conversation has its own record under <c>conversations/</c>.
    /// Model snapshots and native child transcripts are declared gaps.
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
        private JsonArray SaveCallFiles(string callId, byte[] image, IReadOnlyDictionary<string, byte[]> files)
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

        /// <summary>
        /// Keeps a long text once under <c>blobs/</c> and returns its
        /// record-relative path, or null when nothing is written.
        /// </summary>
        public string KeepBlob(string text)
        {
            if (Directory == null)
            {
                return null;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            string relative;
            using (var sha = global::System.Security.Cryptography.SHA256.Create())
            {
                relative = "blobs/" + BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() + ".txt";
            }
            lock (m_Lock)
            {
                try
                {
                    global::System.IO.Directory.CreateDirectory(Path.Combine(Directory, "blobs"));
                    File.WriteAllBytes(Path.Combine(Directory, relative), bytes);
                }
                catch (Exception e)
                {
                    Warn("session record could not keep " + relative + ": " + e.Message);
                    return null;
                }
            }
            return relative;
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

        public void TurnStart(string turnId, string userText)
        {
            lock (m_Lock)
            {
                m_Turn = turnId;
            }
            Record("turn.start", new JsonObject { ["user"] = userText });
        }

        /// <summary>Closes the open turn; events after it carry no turn.</summary>
        public void TurnFinish(JsonObject data)
        {
            Record("turn.finish", data);
            lock (m_Lock)
            {
                m_Turn = null;
            }
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
        /// <paramref name="caller"/> identifies the native agent or an owned
        /// conversation of the external mayor.
        /// </summary>
        public void Function(
            string callId,
            string toolName,
            string arguments,
            AgentToolResult result,
            long elapsedMs,
            ToolAnchors anchors,
            string error = null,
            string caller = null)
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
            if (caller != null)
            {
                data["caller"] = caller;
            }
            if (result.Evidence != null)
            {
                data["evidence"] = result.Evidence.DeepClone();
            }
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
