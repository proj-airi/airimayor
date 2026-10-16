using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentRuntime;
using dotacp.client;
using dotacp.protocol;
using AcpUpdate = dotacp.protocol.SessionUpdate;
using ChatUpdate = AgentRuntime.SessionUpdate;

namespace airimayor.Host
{
    /// <summary>
    /// Chat session that speaks ACP to a child agent and hands city tools
    /// to it as a stdio MCP server.
    /// </summary>
    internal sealed class AcpSession : IAgentSession, IAcpClient
    {
        private readonly Channel<string> m_Pending = Channel.CreateUnbounded<string>();
        private readonly Channel<string> m_Reports = Channel.CreateUnbounded<string>();
        private readonly object m_Lock = new object();
        private readonly List<SnapshotMessage> m_Messages = new List<SnapshotMessage>();
        private readonly Dictionary<string, AcpToolState> m_ToolRows =
            new Dictionary<string, AcpToolState>(StringComparer.Ordinal);
        private readonly SessionRecord m_Record;
        private readonly IAgentTools m_Tools;
        private readonly Func<bool> m_Vision;
        private readonly Func<bool> m_Continue;
        private readonly string m_Continuation;
        private readonly CancellationTokenSource m_Life = new CancellationTokenSource();
        private readonly string m_SessionId;
        private readonly StringBuilder m_Stderr = new StringBuilder();
        private readonly Dictionary<string, string> m_ChildText =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_OwnedSessions = new HashSet<string>(StringComparer.Ordinal);
        private ConversationBook m_Conversations;
        private readonly ToolPreviewLatch m_Previews;
        private ToolBridge m_Bridge;
        private Process m_Process;
        private Connection m_Connection;
        private SessionId m_AcpSessionId;
        private CancellationTokenSource m_Turn;
        private string m_PlanJson = "";
        private string m_Command = "";
        private string m_McpExecutable;
        private string m_WorkingDirectory;
        private bool m_Disposed;

        public AcpSession(
            IAgentTools tools,
            string instructions,
            Func<bool> visionAvailable,
            Func<bool> continueWhenIdle,
            string continuation,
            string command,
            string arguments,
            string mcpExecutable,
            string workingDirectory,
            SessionRecordOptions record,
            Action<string> warn)
        {
            m_Tools = tools;
            m_Vision = visionAvailable ?? (() => false);
            m_Continue = continueWhenIdle ?? (() => false);
            m_Continuation = continuation ?? "";
            m_SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            m_Record = new SessionRecord(m_SessionId, record, warn);
            m_Previews = new ToolPreviewLatch(OnPreviewMatched);
            _ = Task.Run(() => RunAsync(tools, instructions, command, arguments, mcpExecutable, workingDirectory));
        }

        public event Action<ChatUpdate> Updated;

        public SessionRecord Record => m_Record;

        public AgentStatus Status { get; private set; } = AgentStatus.Idle;

        public void Prompt(string text)
        {
            Prompt(text, null, null);
        }

        public void Prompt(string modelText, string displayText, string placesJson)
        {
            if (m_Disposed)
            {
                throw new ObjectDisposedException(nameof(AcpSession));
            }
            string safe = modelText ?? "";
            string shown = displayText ?? safe;
            lock (m_Lock)
            {
                m_Messages.Add(new SnapshotMessage
                {
                    Role = "user",
                    Text = shown,
                    Places = placesJson,
                });
            }
            m_Pending.Writer.TryWrite(safe);
            if (Status == AgentStatus.Thinking || Status == AgentStatus.Working)
            {
                CancelTurn();
            }
            Emit(new ChatUpdate { Kind = "user", Text = shown, Places = placesJson });
        }

        public void Cancel()
        {
            if (m_Disposed)
            {
                return;
            }
            CancelTurn();
            Status = AgentStatus.Interrupted;
            Emit(new ChatUpdate { Kind = "status", Status = AgentStatus.Interrupted, Text = "Current turn interrupted" });
        }

        public string ChatStateJson()
        {
            lock (m_Lock)
            {
                var messages = new JsonArray();
                foreach (SnapshotMessage message in m_Messages)
                {
                    var entry = new JsonObject
                    {
                        ["role"] = message.Role,
                        ["text"] = message.Text ?? "",
                        ["tool"] = message.Tool,
                    };
                    if (!string.IsNullOrEmpty(message.Places))
                    {
                        JsonNode places = JsonNode.Parse(message.Places);
                        if (places != null)
                        {
                            entry["places"] = places;
                        }
                    }
                    messages.Add(entry);
                }
                var state = new JsonObject
                {
                    ["status"] = Status.ToString(),
                    ["busy"] = Status == AgentStatus.Thinking || Status == AgentStatus.Working,
                    ["pendingInputs"] = m_Pending.Reader.Count,
                    ["session"] = m_SessionId,
                    ["plan"] = string.IsNullOrEmpty(m_PlanJson) ? null : JsonNode.Parse(m_PlanJson),
                    ["messages"] = messages,
                };
                return state.ToJsonString();
            }
        }

        public void Dispose()
        {
            if (m_Disposed)
            {
                return;
            }
            m_Disposed = true;
            m_Conversations?.CancelAll();
            m_Life.Cancel();
            CancelTurn();
            try
            {
                m_Connection?.Dispose();
            }
            catch (Exception)
            {
            }
            try
            {
                if (m_Process != null && !m_Process.HasExited)
                {
                    m_Process.Kill();
                }
            }
            catch (Exception)
            {
            }
            m_Process?.Dispose();
            m_Bridge?.Dispose();
            m_Record.Dispose();
            m_Life.Dispose();
        }

        public void OnDisconnected(Connection connection)
        {
            if (m_Disposed)
            {
                return;
            }
            Status = AgentStatus.Error;
            m_Record.Error("acp", "The external agent disconnected.");
            Emit(new ChatUpdate { Kind = "error", Text = "The external agent disconnected." });
        }

        public Task<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This client does not read files.");
        }

        public Task<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This client does not write files.");
        }

        public Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken = default)
        {
            string sessionId = request == null ? "" : request.SessionId.ToString();
            bool known = string.IsNullOrEmpty(sessionId) ||
                string.Equals(sessionId, m_AcpSessionId.ToString(), StringComparison.Ordinal) ||
                m_OwnedSessions.Contains(sessionId);
            if (!known || request?.ToolCall == null || !IsCityTool(request.ToolCall.Title))
            {
                return Task.FromResult(new RequestPermissionResponse
                {
                    Outcome = new RequestPermissionOutcomeCancelled(),
                });
            }
            if (request.Options == null)
            {
                return Task.FromResult(new RequestPermissionResponse
                {
                    Outcome = new RequestPermissionOutcomeCancelled(),
                });
            }
            PermissionOption allow = null;
            foreach (PermissionOption option in request.Options)
            {
                if (option.Kind == PermissionOptionKind.AllowOnce || option.Kind == PermissionOptionKind.AllowAlways)
                {
                    allow = option;
                    break;
                }
            }
            if (allow == null)
            {
                return Task.FromResult(new RequestPermissionResponse
                {
                    Outcome = new RequestPermissionOutcomeCancelled(),
                });
            }
            return Task.FromResult(new RequestPermissionResponse
            {
                Outcome = new SelectedPermissionOutcome { OptionId = allow.OptionId },
            });
        }

        public Task SessionUpdateAsync(SessionNotification notification, CancellationToken cancellationToken = default)
        {
            string sessionId = notification == null ? "" : notification.SessionId.ToString();
            if (!string.IsNullOrEmpty(sessionId) && m_OwnedSessions.Contains(sessionId))
            {
                ApplyChildUpdate(sessionId, notification.Update);
                return Task.CompletedTask;
            }
            ApplyUpdate(notification?.Update);
            return Task.CompletedTask;
        }

        public Task<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This client does not open a terminal.");
        }

        public Task<KillTerminalResponse> KillTerminalAsync(KillTerminalRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This client does not open a terminal.");
        }

        public Task<TerminalOutputResponse> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This client does not open a terminal.");
        }

        public Task<ReleaseTerminalResponse> ReleaseTerminalAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This client does not open a terminal.");
        }

        public Task<WaitForTerminalExitResponse> WaitForTerminalExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This client does not open a terminal.");
        }

        public Task<object> ExtMethodAsync(string method, object request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(method);
        }

        public Task ExtNotificationAsync(string method, object notification, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        private async Task RunAsync(
            IAgentTools tools,
            string instructions,
            string command,
            string arguments,
            string mcpExecutable,
            string workingDirectory)
        {
            try
            {
                m_Command = command ?? "";
                if (string.IsNullOrWhiteSpace(mcpExecutable) || !File.Exists(mcpExecutable))
                {
                    Fail("The city tool server is not installed.");
                    return;
                }
                m_McpExecutable = mcpExecutable;
                m_WorkingDirectory = workingDirectory;
                m_Conversations = new ConversationBook(RunChildSessionAsync, Emit, DeliverReport);
                m_Bridge = ToolBridge.Start(tools, m_Vision, instructions, m_Conversations, m_Previews);
                m_Process = StartAgent(command, arguments);
                m_Connection = Connection.RunClient(this, m_Process.StandardInput.BaseStream, m_Process.StandardOutput.BaseStream);
                if (m_Connection == null)
                {
                    Fail("Could not connect to the external agent.");
                    return;
                }
                await m_Connection.InitializeAsync(new InitializeRequest
                {
                    ProtocolVersion = ProtocolMeta.Version,
                    ClientCapabilities = new ClientCapabilities(),
                    ClientInfo = new Implementation { Name = "AIRI Mayor", Version = "0" },
                }, m_Life.Token).ConfigureAwait(false);
                NewSessionResponse created = await m_Connection.NewSessionAsync(new NewSessionRequest
                {
                    Cwd = workingDirectory,
                    McpServers = CityServers(true),
                }, m_Life.Token).ConfigureAwait(false);
                m_AcpSessionId = created.SessionId;
                await PumpAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (!m_Disposed)
                {
                    Fail(FailureText(e));
                }
            }
        }

        private Process StartAgent(string command, string arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments ?? "",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(command), "opencode", StringComparison.OrdinalIgnoreCase))
            {
                // This process only. OpenCode 1.18.32 keeps JSON key order and the last
                // matching rule wins, so city_* must follow * or the city tools disappear.
                // * also denies task: a child opened inside the agent never becomes an ACP session.
                // mcp_timeout is milliseconds. 45 minutes covers the longest advance:
                // 8 in-game hours at the city-side budget of 5 real minutes each.
                start.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"] =
                    "{\"permission\":{\"*\":\"deny\",\"city_*\":\"allow\"},\"experimental\":{\"mcp_timeout\":2700000}}";
            }
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.ErrorDataReceived += (sender, args) =>
            {
                if (string.IsNullOrEmpty(args.Data))
                {
                    return;
                }
                m_Record.System("acp", args.Data);
                lock (m_Stderr)
                {
                    if (m_Stderr.Length < 2000)
                    {
                        m_Stderr.AppendLine(args.Data);
                    }
                }
            };
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start " + command + ".");
            }
            process.BeginErrorReadLine();
            return process;
        }

        private async Task PumpAsync()
        {
            while (!m_Life.IsCancellationRequested)
            {
                string text;
                try
                {
                    text = await ReadNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                while (!m_Life.IsCancellationRequested)
                {
                    m_Turn = new CancellationTokenSource();
                    Status = AgentStatus.Thinking;
                    Emit(new ChatUpdate { Kind = "status", Status = AgentStatus.Thinking });
                    PromptResponse response;
                    try
                    {
                        response = await m_Connection.PromptAsync(new PromptRequest
                        {
                            SessionId = m_AcpSessionId,
                            Prompt = new ContentBlock[] { new TextContent { Text = text } },
                        }, m_Turn.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        response = new PromptResponse { StopReason = StopReason.Cancelled };
                    }
                    catch (Exception e)
                    {
                        Fail(FailureText(e));
                        return;
                    }
                    if (response.StopReason == StopReason.Cancelled || m_Turn.IsCancellationRequested)
                    {
                        break;
                    }
                    if (m_Reports.Reader.TryRead(out string report))
                    {
                        text = report;
                        continue;
                    }
                    if (response.StopReason != StopReason.EndTurn ||
                        !m_Continue() ||
                        m_Pending.Reader.Count > 0)
                    {
                        Status = AgentStatus.Idle;
                        Emit(new ChatUpdate { Kind = "turn" });
                        Emit(new ChatUpdate { Kind = "status", Status = AgentStatus.Idle });
                        break;
                    }
                    text = m_Continuation;
                }
            }
        }

        private void DeliverReport(string text)
        {
            if (m_Disposed)
            {
                return;
            }
            m_Reports.Writer.TryWrite(text ?? "");
        }

        private async Task<string> ReadNextAsync()
        {
            while (!m_Life.IsCancellationRequested)
            {
                if (m_Pending.Reader.TryRead(out string player))
                {
                    return player;
                }
                if (m_Reports.Reader.TryRead(out string report))
                {
                    return report;
                }
                Task<bool> playerReady = m_Pending.Reader.WaitToReadAsync(m_Life.Token).AsTask();
                Task<bool> reportReady = m_Reports.Reader.WaitToReadAsync(m_Life.Token).AsTask();
                await Task.WhenAny(playerReady, reportReady).ConfigureAwait(false);
            }
            throw new OperationCanceledException();
        }

        private void ApplyUpdate(AcpUpdate update)
        {
            if (update is SessionUpdateAgentMessageChunk chunk && chunk.Content is TextContent text)
            {
                AppendAssistant(text.Text);
                Emit(AcpChatMap.AgentText(text.Text));
                return;
            }
            if (update is ToolCall call)
            {
                ApplyTool(call.ToolCallId, call.Title, call.Status, call.RawInput, call.Content, call.RawOutput);
                return;
            }
            if (update is SessionUpdateToolCallUpdate toolUpdate)
            {
                ApplyTool(
                    toolUpdate.ToolCallId,
                    toolUpdate.Title,
                    toolUpdate.Status,
                    toolUpdate.RawInput,
                    toolUpdate.Content,
                    toolUpdate.RawOutput);
                return;
            }
            if (update is Plan plan)
            {
                var steps = new List<PlanStep>();
                if (plan.Entries != null)
                {
                    foreach (PlanEntry entry in plan.Entries)
                    {
                        steps.Add(new PlanStep
                        {
                            Content = entry.Content,
                            Priority = PriorityText(entry.Priority),
                            Status = StatusText(entry.Status),
                        });
                    }
                }
                string json = AcpChatMap.PlanJson(steps);
                lock (m_Lock)
                {
                    m_PlanJson = json;
                }
                Emit(AcpChatMap.Plan(json));
            }
        }

        private static string PriorityText(PlanEntryPriority priority)
        {
            if (priority == PlanEntryPriority.High)
            {
                return "high";
            }
            if (priority == PlanEntryPriority.Low)
            {
                return "low";
            }
            return "medium";
        }

        private static string StatusText(PlanEntryStatus status)
        {
            if (status == PlanEntryStatus.InProgress)
            {
                return "in_progress";
            }
            if (status == PlanEntryStatus.Completed)
            {
                return "completed";
            }
            return "pending";
        }

        private void ApplyTool(
            ToolCallId id,
            string title,
            ToolCallStatus status,
            object rawInput,
            ToolCallContent[] content,
            object rawOutput)
        {
            string key = id.ToString();
            bool finished = status == ToolCallStatus.Completed || status == ToolCallStatus.Failed;
            var patch = new AcpToolPatch
            {
                HasTitle = title != null,
                Title = title,
                HasRawInput = rawInput != null,
                RawInput = JsonText(rawInput),
                HasContent = content != null,
                Content = content == null ? null : ToolText(content),
                HasRawOutput = rawOutput != null,
                RawOutput = JsonText(rawOutput),
                HasStatus = true,
                Finished = finished,
                Failed = status == ToolCallStatus.Failed,
            };
            AcpToolState state;
            bool first;
            lock (m_Lock)
            {
                first = !m_ToolRows.TryGetValue(key, out state);
                state = AcpToolState.Apply(state, key, patch);
                m_ToolRows[key] = state;
                if (state.Finished)
                {
                    RememberTool(state);
                }
            }
            if (first)
            {
                Status = AgentStatus.Working;
                Emit(new ChatUpdate { Kind = "status", Status = AgentStatus.Working });
            }
            string image = m_Previews.Claim(key, state.Title, state.RawInput);
            ChatUpdate row = AcpChatMap.ToolRow(state);
            row.Image = image;
            Emit(row);
        }

        private void OnPreviewMatched(string callId, string dataUri)
        {
            AcpToolState state;
            lock (m_Lock)
            {
                if (!m_ToolRows.TryGetValue(callId, out state))
                {
                    return;
                }
            }
            ChatUpdate row = AcpChatMap.ToolRow(state);
            row.Image = dataUri;
            Emit(row);
        }

        private void RememberTool(AcpToolState state)
        {
            for (int index = m_Messages.Count - 1; index >= 0; index--)
            {
                if (m_Messages[index].CallId == state.CallId)
                {
                    m_Messages[index].Tool = state.Title;
                    m_Messages[index].Text = state.Content ?? "";
                    return;
                }
            }
            m_Messages.Add(new SnapshotMessage
            {
                Role = "tool",
                Tool = state.Title,
                Text = state.Content ?? "",
                CallId = state.CallId,
            });
        }

        private static string JsonText(object value)
        {
            if (value == null)
            {
                return null;
            }
            if (value is JToken token)
            {
                return token.ToString(Formatting.None);
            }
            if (value is string text)
            {
                return text;
            }
            return JsonConvert.SerializeObject(value);
        }

        private static string ToolText(ToolCallContent[] content)
        {
            var builder = new StringBuilder();
            foreach (ToolCallContent item in content)
            {
                if (item is Content block && block.ContentValue is TextContent text && !string.IsNullOrEmpty(text.Text))
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }
                    builder.Append(text.Text);
                }
            }
            return builder.ToString();
        }

        private bool IsCityTool(string title)
        {
            if (string.IsNullOrEmpty(title) || m_Tools == null)
            {
                return false;
            }
            foreach (AgentToolDeclaration tool in m_Tools.List(m_Vision()))
            {
                if (string.Equals(tool.Name, title, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return title.StartsWith("city", StringComparison.OrdinalIgnoreCase);
        }

        private void AppendAssistant(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            lock (m_Lock)
            {
                if (m_Messages.Count > 0 && m_Messages[m_Messages.Count - 1].Role == "assistant")
                {
                    m_Messages[m_Messages.Count - 1].Text += text;
                    return;
                }
                m_Messages.Add(new SnapshotMessage { Role = "assistant", Text = text });
            }
        }

        private void CancelTurn()
        {
            try
            {
                m_Turn?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            if (m_Connection != null)
            {
                _ = m_Connection.CancelAsync(new CancelNotification { SessionId = m_AcpSessionId });
            }
        }

        private McpServer[] CityServers(bool tasks)
        {
            return new McpServer[]
            {
                new McpServerStdio
                {
                    Name = "city",
                    Command = m_McpExecutable,
                    Args = new[] { "--base-url", m_Bridge.BaseUrl, "--token", tasks ? m_Bridge.Token : m_Bridge.ChildToken },
                    Env = new EnvVariable[0],
                },
            };
        }

        private async Task<string> RunChildSessionAsync(string id, string message, CancellationToken cancellationToken)
        {
            if (m_Connection == null)
            {
                throw new InvalidOperationException("The external agent is not connected.");
            }
            NewSessionResponse created = await m_Connection.NewSessionAsync(new NewSessionRequest
            {
                Cwd = m_WorkingDirectory,
                McpServers = CityServers(false),
            }, cancellationToken).ConfigureAwait(false);
            string acpId = created.SessionId.ToString();
            lock (m_Lock)
            {
                m_OwnedSessions.Add(acpId);
                m_ChildText[acpId] = "";
            }
            using (cancellationToken.Register(() =>
            {
                try
                {
                    _ = m_Connection.CancelAsync(new CancelNotification { SessionId = created.SessionId });
                }
                catch (Exception)
                {
                }
            }))
            {
                await m_Connection.PromptAsync(new PromptRequest
                {
                    SessionId = created.SessionId,
                    Prompt = new ContentBlock[] { new TextContent { Text = message } },
                }, cancellationToken).ConfigureAwait(false);
            }
            lock (m_Lock)
            {
                m_ChildText.TryGetValue(acpId, out string text);
                return text ?? "";
            }
        }

        private void ApplyChildUpdate(string sessionId, AcpUpdate update)
        {
            if (update is SessionUpdateAgentMessageChunk chunk && chunk.Content is TextContent text)
            {
                lock (m_Lock)
                {
                    m_ChildText.TryGetValue(sessionId, out string soFar);
                    m_ChildText[sessionId] = (soFar ?? "") + (text.Text ?? "");
                }
                return;
            }
            if (update is ToolCall call)
            {
                ApplyTool(call.ToolCallId, call.Title, call.Status, call.RawInput, call.Content, call.RawOutput);
                return;
            }
            if (update is SessionUpdateToolCallUpdate toolUpdate)
            {
                ApplyTool(
                    toolUpdate.ToolCallId,
                    toolUpdate.Title,
                    toolUpdate.Status,
                    toolUpdate.RawInput,
                    toolUpdate.Content,
                    toolUpdate.RawOutput);
            }
        }

        private string FailureText(Exception error)
        {
            if (error is Win32Exception missing && (missing.NativeErrorCode == 2 || missing.NativeErrorCode == 3))
            {
                return "Command not found: " + m_Command + ".";
            }
            if (m_Process != null && m_Process.HasExited)
            {
                string stderr;
                lock (m_Stderr)
                {
                    stderr = m_Stderr.ToString().Trim();
                }
                if (stderr.Length == 0)
                {
                    return "The agent process exited.";
                }
                return "The agent process exited. " + stderr;
            }
            string message = error.Message ?? "";
            if (!IsAuth(message))
            {
                return message;
            }
            string text = message.Trim();
            if (text.StartsWith("Not signed in", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
            return text.Length == 0 ? "Not signed in." : "Not signed in. " + text;
        }

        private static bool IsAuth(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string lower = text.ToLowerInvariant();
            return lower.Contains("sign in")
                || lower.Contains("signin")
                || lower.Contains("log in")
                || lower.Contains("login")
                || lower.Contains("not logged")
                || lower.Contains("authenticate")
                || lower.Contains("authentication")
                || lower.Contains("unauthorized");
        }

        private void Fail(string message)
        {
            string text = message ?? "";
            Status = AgentStatus.Error;
            m_Record.Error("acp", text);
            Emit(new ChatUpdate { Kind = "error", Text = text });
            Emit(new ChatUpdate { Kind = "status", Status = AgentStatus.Error, Text = text });
        }

        private void Emit(ChatUpdate update)
        {
            Updated?.Invoke(update);
        }

        private sealed class SnapshotMessage
        {
            public string Role;
            public string Text;
            public string Tool;
            public string CallId;
            public string Places;
        }
    }
}
