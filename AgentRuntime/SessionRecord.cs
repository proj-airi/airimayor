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
    /// task/turn/generation/function/compact/error events, and
    /// <c>model-calls.jsonl</c> one line per model request. A text too long
    /// to repeat on every request is kept once under <c>blobs/</c>, a tool
    /// catalog under <c>catalogs/</c>, both named by SHA-256. Files a tool
    /// call returned live under <c>calls/&lt;call&gt;/</c>, and each owned
    /// conversation has its own record under <c>conversations/</c>.
    /// Every line of both logs carries the same envelope: schema, seq, ts,
    /// session, turn, an optional call, and the simulation clock while a city
    /// runs. Events between turns carry no turn. API keys never appear.
    /// </summary>
    public sealed class SessionRecord : IDisposable
    {
        public const int Schema = 1;
        public const string SessionFile = "session.json";
        public const string TimelineFile = "timeline.jsonl";
        public const string ModelCallsFile = "model-calls.jsonl";

        /// <summary>A text at least this long is kept once as a blob and referenced.</summary>
        public const int BlobChars = 2048;

        private readonly object m_Lock = new object();
        private readonly string m_SessionId;
        private readonly Action<string> m_Warn;
        private readonly Func<SimulationStamp> m_Clock;
        private readonly JsonObject m_Facts;
        private readonly JsonlFile m_Timeline;
        private readonly JsonlFile m_ModelCalls;
        private readonly HashSet<string> m_Kept = new HashSet<string>(StringComparer.Ordinal);
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
                m_ModelCalls = new JsonlFile(
                    Path.Combine(directory, ModelCallsFile), warn, rotated => DeclareGap(rotated + " rotated out"));
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
            Append(m_Timeline, type, data, call);
        }

        /// <summary>One model request: what was sent, what came back, and its anchors.</summary>
        public void ModelCall(JsonObject data)
        {
            Append(m_ModelCalls, "model.call", data, null);
        }

        /// <summary>
        /// Keeps a long text once under <c>blobs/</c> and returns its
        /// record-relative path, or null when nothing is written.
        /// </summary>
        public string KeepBlob(string text)
        {
            return Keep("blobs", ".txt", Encoding.UTF8.GetBytes(text ?? ""));
        }

        /// <summary>
        /// Keeps a tool catalog once under <c>catalogs/</c> and returns its
        /// SHA-256, which a model call names.
        /// </summary>
        public string KeepCatalog(JsonArray catalog)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(catalog.ToJsonString());
            Keep("catalogs", ".json", bytes);
            return Sha256(bytes);
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = global::System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        private string Keep(string folder, string extension, byte[] bytes)
        {
            if (Directory == null)
            {
                return null;
            }
            string relative = folder + "/" + Sha256(bytes) + extension;
            lock (m_Lock)
            {
                if (m_Kept.Contains(relative))
                {
                    return relative;
                }
                try
                {
                    global::System.IO.Directory.CreateDirectory(Path.Combine(Directory, folder));
                    File.WriteAllBytes(Path.Combine(Directory, relative), bytes);
                    m_Kept.Add(relative);
                }
                catch (Exception e)
                {
                    Warn("session record could not keep " + relative + ": " + e.Message);
                    return null;
                }
            }
            return relative;
        }

        private void Append(JsonlFile file, string type, JsonObject data, string call)
        {
            lock (m_Lock)
            {
                if (file == null || m_Disposed)
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
                file.Append(line);
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

        /// <summary>Closes the open turn; events after it carry no turn.</summary>
        public void TurnFinish(JsonObject data)
        {
            Record("turn.finish", data);
            lock (m_Lock)
            {
                m_Turn = null;
            }
        }

        /// <summary>Timeline summary of one model round; the request and reply are in the model call it names.</summary>
        public void Generation(string modelCall, string model, string reasoning, JsonArray toolCalls, JsonObject usage, long elapsedMs)
        {
            Record("generation", new JsonObject
            {
                ["modelCall"] = modelCall,
                ["model"] = model,
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
        /// <paramref name="caller"/> is <c>conversation</c> when an owned
        /// conversation of the external mayor made the call.
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
                m_ModelCalls?.Dispose();
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
