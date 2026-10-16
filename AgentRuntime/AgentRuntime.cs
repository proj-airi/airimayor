using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AgentRuntime
{
    public enum AgentStatus
    {
        Idle,
        Thinking,
        Working,
        Interrupted,
        Error,
    }

    /// <summary>UI-facing event emitted by the agent loop.</summary>
    public sealed class SessionUpdate
    {
        public string Kind;      // status|delta|tool|user|error|compact|turn|progress|plan
        public string Text;
        public string Tool;
        public AgentStatus Status;

        /// <summary>ACP tool call id. Absent on built-in tool events.</summary>
        public string CallId;

        /// <summary>ACP tool content text. Absent until the agent sends content.</summary>
        public string Result;

        /// <summary>ACP rawOutput JSON. Absent until the agent sends rawOutput.</summary>
        public string Output;

        /// <summary>UI-only image preview (data URI) for image tool results.</summary>
        public string Image;

        /// <summary>JSON array of pointed places on a player message. Absent otherwise.</summary>
        public string Places;

        public string ToJsonString()
        {
            var obj = new JsonObject
            {
                ["kind"] = Kind ?? "",
                ["text"] = Text ?? "",
                ["status"] = Status.ToString(),
            };
            if (Tool != null)
            {
                obj["tool"] = Tool;
            }
            if (CallId != null)
            {
                obj["callId"] = CallId;
            }
            if (Result != null)
            {
                obj["result"] = Result;
            }
            if (Output != null)
            {
                obj["output"] = Output;
            }
            if (Image != null)
            {
                obj["image"] = Image;
            }
            if (!string.IsNullOrEmpty(Places))
            {
                JsonNode places = JsonNode.Parse(Places);
                if (places != null)
                {
                    obj["places"] = places;
                }
            }
            return obj.ToJsonString();
        }
    }

    internal sealed class AgentInput
    {
        public string Text;
        public bool Report;
        public string DisplayText;
        public string PlacesJson;
    }

    internal sealed class PlaceNote
    {
        public string DisplayText;
        public string PlacesJson;
    }

    /// <summary>
    /// In-process agent runtime: IChatClient + hand-rolled function-calling
    /// loop. One user message runs one turn. The plan stays in the session
    /// until the next set_plan. When continuation is on, a turn that becomes
    /// idle with no pending player text opens another turn. A turn ends
    /// when the model stops calling tools, a generation times out, or the
    /// player steers or interrupts. Player messages leave the plan in place.
    /// </summary>
    public sealed class AgentRuntime : IAgentSession
    {
        private const string CompactionTaskPrompt = @"COMPACTION TASK:
Ignore the normal assistant response format for this response.
Do not call tools. Do not emit tool_calls, DSML, XML tags, or function-call markup.
Return strict JSON with:
{
  ""time_anchor"": string,
  ""session_state"": string,
  ""active_commitments"": string[],
  ""durable_facts"": string[],
  ""relevant_people"": string[],
  ""open_loops"": string[],
  ""recent_timeline"": string[],
  ""forgettable_noise"": string[],
  ""paused_state"": string,
  ""last_world_snapshot"": string
}
Preserve player constraints, player instructions, open loops, important names,
and current world/session state
in durable_facts. Prefer compressing assistant chatter, tool chatter, and stale
notices. Do not keep stale relative-time phrases; convert them into stable facts
or timeline notes. Keep each list item short and concrete.";

        private const string SummaryPrefix = "[context summary] ";
        private const string TaskReportAuthor = "task";

        private readonly Channel<AgentInput> m_Pending = Channel.CreateUnbounded<AgentInput>();
        private readonly List<ChatMessage> m_History = new List<ChatMessage>();
        private readonly Dictionary<ChatMessage, PlaceNote> m_PlaceNotes =
            new Dictionary<ChatMessage, PlaceNote>();
        private readonly object m_Lock = new object();
        private readonly SessionRecord m_Record;
        private readonly AgentToolSurface m_ToolSurface;
        private readonly AgentPromptAssembler m_PromptAssembler;
        private readonly AgentToolExecutor m_ToolExecutor;
        private readonly SessionPlan m_Plan = new SessionPlan();

        private readonly AgentClientFactory m_ClientFactory;
        private readonly Func<ModelSettings> m_ReadModel;
        private readonly IAgentTools m_Tools;
        private readonly string m_SystemPrompt;
        private readonly IChatClient m_ChatClient;
        private readonly Action<string> m_Warn;
        private readonly ConversationBook m_Conversations;
        private readonly object m_CancelGate = new object();
        private Task m_LoopTask;
        private CancellationTokenSource m_TurnCts;
        private CancellationTokenSource m_GenerationCts;
        private CancellationTokenSource m_LoopCts = new CancellationTokenSource();
        private string m_SessionId;
        private string m_TurnId;
        private long m_EstimatedTokens;
        private int m_TurnGenerationCount;
        private UsageDetails m_TurnUsage;
        private AgentUsageJson.Coverage m_TurnUsageCoverage;
        private bool m_TimeoutOccurred;
        private bool m_Disposed;

        public AgentRuntime(
            IAgentTools tools,
            string systemPrompt,
            Func<ModelSettings> readModel,
            SessionRecordOptions record,
            Action<string> warn = null)
            : this(tools, systemPrompt, readModel, record, null, warn)
        {
        }

        internal AgentRuntime(
            IAgentTools tools,
            string systemPrompt,
            Func<ModelSettings> readModel,
            SessionRecordOptions record,
            IChatClient chatClient,
            Action<string> warn = null,
            bool enableTasks = true)
        {
            m_ReadModel = readModel;
            m_Tools = tools;
            m_SystemPrompt = systemPrompt;
            m_ChatClient = chatClient;
            m_Warn = warn;
            m_SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            m_Conversations = enableTasks
                ? new ConversationBook(RunChildTurnAsync, Emit, EnqueueReport)
                : null;
            m_Record = new SessionRecord(m_SessionId, record, warn);
            m_Record.Describe("prompt", new JsonObject
            {
                ["blob"] = m_Record.KeepBlob(systemPrompt),
                ["chars"] = (systemPrompt ?? "").Length,
            });
            m_ClientFactory = new AgentClientFactory(
                m_Record, m_SessionId, readModel, CaptureReasoningSnapshots, chatClient);
            m_ToolSurface = new AgentToolSurface(tools, m_Conversations, m_Plan, EmitPlan);
            m_PromptAssembler = new AgentPromptAssembler(systemPrompt, SummaryPrefix);
            m_ToolExecutor = new AgentToolExecutor(
                m_ToolSurface,
                m_ClientFactory,
                m_Record,
                Emit,
                AppendHistoryMessage);
        }

        public event Action<SessionUpdate> Updated;

        /// <summary>This session's record. City code writes its notes here while the session is live.</summary>
        public SessionRecord Record => m_Record;

        public AgentStatus Status { get; private set; } = AgentStatus.Idle;

        public bool IsBusy => Status == AgentStatus.Thinking || Status == AgentStatus.Working;

        /// <summary>
        /// Queue a player message. A reply in progress stops. A tool already
        /// running finishes. The plan stays.
        /// </summary>
        public void Prompt(string text)
        {
            Prompt(text, null, null);
        }

        public void Prompt(string modelText, string displayText, string placesJson)
        {
            if (m_Disposed)
            {
                throw new ObjectDisposedException(nameof(AgentRuntime));
            }
            string safe = modelText ?? "";
            string shown = displayText ?? safe;
            bool thinking = Status == AgentStatus.Thinking;
            m_Pending.Writer.TryWrite(new AgentInput
            {
                Text = safe,
                DisplayText = displayText,
                PlacesJson = placesJson,
            });
            if (thinking)
            {
                CancelGeneration();
            }
            Emit(new SessionUpdate { Kind = "user", Text = shown, Places = placesJson });
            EnsureLoop();
        }

        public void Cancel()
        {
            if (m_Disposed)
            {
                return;
            }
            m_TurnCts?.Cancel();
            CancelGeneration();
            Status = AgentStatus.Interrupted;
            Emit(new SessionUpdate { Kind = "status", Status = AgentStatus.Interrupted, Text = "Current turn interrupted" });
        }

        private void EnqueueReport(string text)
        {
            if (m_Disposed)
            {
                return;
            }
            m_Pending.Writer.TryWrite(new AgentInput { Text = text ?? "", Report = true });
            EnsureLoop();
        }

        public string ChatStateJson()
        {
            lock (m_Lock)
            {
                PrunePlaceNotes();
                // Tool results only carry CallId; resolve names from prior calls.
                var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (ChatMessage message in m_History)
                {
                    foreach (AIContent content in message.Contents)
                    {
                        if (content is FunctionCallContent call && !string.IsNullOrEmpty(call.CallId))
                        {
                            callNames[call.CallId] = call.Name ?? call.CallId;
                        }
                    }
                }

                var messages = new JsonArray();
                foreach (ChatMessage message in m_History)
                {
                    // System prompt / compaction stay in the model history
                    // only — do not dump them into the chat UI.
                    if (message.Role == ChatRole.System)
                    {
                        continue;
                    }
                    if (message.Role == ChatRole.User &&
                        message.Contents.Any(content => content is DataContent))
                    {
                        continue;
                    }
                    bool report = message.Role == ChatRole.User && message.AuthorName == TaskReportAuthor;
                    string role = message.Role == ChatRole.Assistant ? "assistant"
                        : message.Role == ChatRole.Tool || report ? "tool"
                        : "user";
                    string text = message.Text ?? "";
                    string tool = null;

                    if (report)
                    {
                        tool = TaskReportAuthor;
                        text = TruncateForLog(message.Text ?? "", 800);
                    }
                    else if (role == "tool")
                    {
                        foreach (AIContent content in message.Contents)
                        {
                            if (content is FunctionResultContent result)
                            {
                                if (!string.IsNullOrEmpty(result.CallId) &&
                                    callNames.TryGetValue(result.CallId, out string name))
                                {
                                    tool = name;
                                }
                                else
                                {
                                    tool = result.CallId ?? "tool";
                                }
                                text = result.Result?.ToString() ?? text;
                                break;
                            }
                        }
                        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrEmpty(tool))
                        {
                            continue;
                        }
                        text = TruncateForLog(text ?? "", 800);
                    }
                    else if (role == "assistant")
                    {
                        // Pure function-call turns are shown via the following tool rows.
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }
                        tool = ToolCallNames(message);
                    }

                    var entry = new JsonObject
                    {
                        ["role"] = role,
                        ["text"] = text,
                        ["tool"] = tool,
                    };
                    if (role == "user" && m_PlaceNotes.TryGetValue(message, out PlaceNote note))
                    {
                        entry["text"] = note.DisplayText ?? "";
                        JsonNode places = JsonNode.Parse(note.PlacesJson);
                        if (places != null)
                        {
                            entry["places"] = places;
                        }
                    }
                    messages.Add(entry);
                }
                AgentModelProfile profile = m_ClientFactory.GetProfile();
                string planJson = m_Plan.ToUiJson();
                var state = new JsonObject
                {
                    ["status"] = Status.ToString(),
                    ["busy"] = IsBusy,
                    ["pendingInputs"] = m_Pending.Reader.Count,
                    ["session"] = m_SessionId,
                    ["turn"] = m_TurnId,
                    ["context"] = new JsonObject
                    {
                        ["windowTokens"] = profile.ContextWindowTokens,
                        ["estimatedTokens"] = m_EstimatedTokens,
                        ["compactAtTokens"] = profile.CompactAtTokens,
                        ["source"] = profile.Source,
                        ["vision"] = profile.VisionAvailable,
                    },
                    ["plan"] = string.IsNullOrEmpty(planJson) ? null : JsonNode.Parse(planJson),
                    ["messages"] = messages,
                };
                return state.ToJsonString();
            }
        }

        private void PrunePlaceNotes()
        {
            if (m_PlaceNotes.Count == 0)
            {
                return;
            }
            var live = new HashSet<ChatMessage>(m_History);
            var stale = new List<ChatMessage>();
            foreach (ChatMessage key in m_PlaceNotes.Keys)
            {
                if (!live.Contains(key))
                {
                    stale.Add(key);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                m_PlaceNotes.Remove(stale[i]);
            }
        }

        private void EnsureLoop()
        {
            if (m_LoopTask == null || m_LoopTask.IsCompleted)
            {
                m_LoopTask = RunLoopAsync();
            }
        }

        private async Task RunLoopAsync()
        {
            ModelSettings settings = ReadModel();
            AgentModelProfile profile = m_ClientFactory.GetProfile();
            m_Record.TaskStart(
                settings.Model,
                profile.ContextWindowTokens,
                (double)profile.CompactAtTokens / profile.ContextWindowTokens,
                settings.Wire.ToString());
            while (!m_LoopCts.IsCancellationRequested)
            {
                AgentInput first;
                try
                {
                    first = await m_Pending.Reader.ReadAsync(m_LoopCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                m_TurnCts = new CancellationTokenSource();
                AgentInput current = first;
                while (!m_LoopCts.IsCancellationRequested &&
                    !m_TurnCts.IsCancellationRequested)
                {
                    await RunTurnAsync(current);
                    bool wantAuto = ReadModel().ContinueWhenIdle &&
                        !m_TimeoutOccurred &&
                        m_Pending.Reader.Count == 0 &&
                        !m_TurnCts.IsCancellationRequested &&
                        !m_LoopCts.IsCancellationRequested;
                    if (!wantAuto)
                    {
                        break;
                    }
                    current = null;
                }
            }
        }

        /// <summary>
        /// Runs one turn for a user input, or an autonomous follow-up turn
        /// when input is null.
        /// </summary>
        private async Task RunTurnAsync(AgentInput input)
        {
            bool autonomous = input == null;
            m_TurnId = Guid.NewGuid().ToString("N").Substring(0, 8);
            m_TurnGenerationCount = 0;
            m_TurnUsage = new UsageDetails();
            m_TurnUsageCoverage = new AgentUsageJson.Coverage();
            m_TimeoutOccurred = false;
            m_ToolExecutor.Reset();
            Stopwatch turnTimer = Stopwatch.StartNew();

            try
            {
                if (autonomous)
                {
                    m_Record.TurnStart(m_TurnId, "(autonomous continuation)");
                }
                else if (!string.IsNullOrWhiteSpace(input.Text))
                {
                    var message = new ChatMessage(ChatRole.User, input.Text);
                    if (input.Report)
                    {
                        message.AuthorName = TaskReportAuthor;
                    }
                    lock (m_Lock)
                    {
                        if (!string.IsNullOrEmpty(input.PlacesJson))
                        {
                            m_PlaceNotes[message] = new PlaceNote
                            {
                                DisplayText = input.DisplayText ?? "",
                                PlacesJson = input.PlacesJson,
                            };
                        }
                        m_History.Add(message);
                    }
                    m_Record.TurnStart(m_TurnId, input.Text);
                }
                else
                {
                    m_Record.TurnStart(m_TurnId, "");
                }

                while (!m_TurnCts.IsCancellationRequested)
                {
                    if (m_Pending.Reader.Count > 0)
                    {
                        break;
                    }
                    lock (m_Lock)
                    {
                        m_PromptAssembler.Apply(m_History);
                    }
                    var round = await RunModelRoundAsync(m_TurnCts.Token);
                    if (round.IsError || round.IsPlayerMessage)
                    {
                        break;
                    }

                    if (round.ToolCalls.Count > 0)
                    {
                        await m_ToolExecutor.ExecuteAsync(round.ToolCalls, m_TurnCts.Token);
                        UpdateTokenEstimate();
                        await MaybeCompactAsync(m_TurnCts.Token);
                        if (m_Pending.Reader.Count > 0)
                        {
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // interrupted
            }
            catch (Exception e)
            {
                m_Record.Error("loop", e.ToString());
                Emit(new SessionUpdate
                {
                    Kind = "error",
                    Text = "Loop error: " + SessionRecord.RedactSecrets(e.Message),
                });
            }

            turnTimer.Stop();
            m_Record.TurnFinish(new JsonObject
            {
                ["generations"] = m_TurnGenerationCount,
                ["functions"] = m_ToolExecutor.FunctionCount,
                ["elapsedMs"] = turnTimer.ElapsedMilliseconds,
                ["usage"] = AgentUsageJson.Serialize(m_TurnUsage),
                ["coverage"] = AgentUsageJson.SerializeCoverage(m_TurnUsageCoverage),
            });
            Status = AgentStatus.Idle;
            Emit(new SessionUpdate { Kind = "status", Status = AgentStatus.Idle });
            Emit(new SessionUpdate { Kind = "turn", Text = m_TurnId });
        }

        private void AppendHistoryMessage(ChatMessage message)
        {
            lock (m_Lock)
            {
                m_History.Add(message);
            }
        }

        /// <summary>
        /// Snapshots stored reasoning per assistant message for the request
        /// pipeline, which echoes it back as <c>reasoning_content</c>.
        /// Messages without reasoning are omitted so the policy can skip
        /// untouched requests without parsing them.
        /// </summary>
        private IReadOnlyList<ReasoningEchoSnapshot> CaptureReasoningSnapshots()
        {
            lock (m_Lock)
            {
                var snapshots = new List<ReasoningEchoSnapshot>();
                foreach (ChatMessage message in m_History)
                {
                    if (message.Role != ChatRole.Assistant)
                    {
                        continue;
                    }
                    StringBuilder reasoning = null;
                    List<string> callIds = null;
                    foreach (AIContent content in message.Contents)
                    {
                        if (content is TextReasoningContent reasoningContent &&
                            !string.IsNullOrEmpty(reasoningContent.Text))
                        {
                            if (reasoning == null)
                            {
                                reasoning = new StringBuilder();
                            }
                            reasoning.Append(reasoningContent.Text);
                        }
                        else if (content is FunctionCallContent call && !string.IsNullOrEmpty(call.CallId))
                        {
                            if (callIds == null)
                            {
                                callIds = new List<string>();
                            }
                            callIds.Add(call.CallId);
                        }
                    }
                    if (reasoning == null)
                    {
                        continue;
                    }
                    snapshots.Add(new ReasoningEchoSnapshot(
                        message.Text ?? "",
                        callIds?.ToArray() ?? new string[0],
                        reasoning.ToString()));
                }
                return snapshots;
            }
        }

        private async Task<ModelRound> RunModelRoundAsync(
            CancellationToken cancellationToken,
            bool allowContextRetry = true)
        {
            await MaybeCompactAsync(cancellationToken);
            if (m_Pending.Reader.Count > 0)
            {
                return ModelRound.PlayerMessage();
            }

            IChatClient client = m_ClientFactory.GetClient();
            if (client == null)
            {
                Emit(new SessionUpdate
                {
                    Kind = "error",
                    Text = "Model not configured.",
                });
                return ModelRound.Error("no client");
            }

            AgentModelProfile profile = m_ClientFactory.GetProfile();
            ModelSettings settings = ReadModel();
            ChatOptions options = ModelRequest.Options(
                settings.Model,
                profile,
                m_ToolSurface.Build(profile));
            ModelCall modelCall;
            lock (m_Lock)
            {
                modelCall = ModelCall.Begin(m_Record, "turn", allowContextRetry ? 1 : 2, settings, m_History, options);
            }

            var updates = new List<ChatResponseUpdate>();
            var pendingDelta = new StringBuilder();
            Stopwatch timer = Stopwatch.StartNew();
            var generationCts = new CancellationTokenSource();
            try
            {
                Status = AgentStatus.Thinking;
                ArmGeneration(generationCts);
                Emit(new SessionUpdate { Kind = "status", Status = AgentStatus.Thinking });
                using (CancellationTokenSource timeoutCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generationCts.Token))
                {
                    timeoutCts.CancelAfter(AgentClientFactory.ModelRequestTimeout);
                    await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                        m_History, options, timeoutCts.Token))
                    {
                        updates.Add(update);
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            pendingDelta.Append(update.Text);
                        }
                    }
                }
                if (pendingDelta.Length > 0)
                {
                    Emit(new SessionUpdate { Kind = "delta", Text = pendingDelta.ToString() });
                }
                timer.Stop();

                ChatResponse response = updates.ToChatResponse();
                IList<ChatMessage> responseMessages = response.Messages ?? new List<ChatMessage>();
                lock (m_Lock)
                {
                    m_History.AddRange(responseMessages);
                }
                m_TurnGenerationCount++;

                var toolCalls = new List<FunctionCallContent>();
                if (m_History.Count > 0)
                {
                    ChatMessage last;
                    lock (m_Lock)
                    {
                        last = m_History[m_History.Count - 1];
                    }
                    foreach (AIContent content in last.Contents)
                    {
                        if (content is FunctionCallContent call)
                        {
                            toolCalls.Add(call);
                        }
                    }
                }

                UpdateTokenEstimate();
                string reasoning = CollectReasoning(updates);
                modelCall.Completed(response, reasoning, toolCalls);
                EmitGeneration(modelCall.Id, response, toolCalls, reasoning, timer.ElapsedMilliseconds);
                return new ModelRound
                {
                    Text = response.Text ?? "",
                    ToolCalls = toolCalls,
                    Usage = response.Usage,
                };
            }
            catch (OperationCanceledException)
            {
                if (generationCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    modelCall.Ended("cancelled");
                    return ModelRound.PlayerMessage();
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    timer.Stop();
                    modelCall.Ended("timeout");
                    m_TimeoutOccurred = true;
                    m_Record.Error(
                        "generation-timeout",
                        "model response exceeded " + AgentClientFactory.ModelRequestTimeoutSeconds + "s");
                    Emit(new SessionUpdate
                    {
                        Kind = "error",
                        Text = "Model response timed out (" + AgentClientFactory.ModelRequestTimeoutSeconds +
                            "s); this turn stopped with no autonomous continuation. Please retry.",
                    });
                    return ModelRound.Error("model response timeout");
                }
                modelCall.Ended("cancelled");
                throw;
            }
            catch (Exception e)
            {
                timer.Stop();
                modelCall.Ended("failed", e);
                if (allowContextRetry && IsContextLengthError(e.ToString()))
                {
                    int historyCount = m_History.Count;
                    long estimateBefore = m_EstimatedTokens;
                    m_Record.Error("generation-context", e.ToString());
                    await MaybeCompactAsync(cancellationToken, true);
                    if (m_History.Count < historyCount || m_EstimatedTokens < estimateBefore)
                    {
                        return await RunModelRoundAsync(cancellationToken, false);
                    }
                }
                m_Record.Error("generation", e.ToString());
                string safeMessage = SessionRecord.RedactSecrets(e.Message);
                Emit(new SessionUpdate { Kind = "error", Text = "Model call failed: " + safeMessage });
                return ModelRound.Error(safeMessage);
            }
            finally
            {
                DisarmGeneration(generationCts);
            }
        }

        private static bool IsContextLengthError(string message)
        {
            string normalized = (message ?? "").ToLowerInvariant();
            return normalized.Contains("context_length_exceeded") ||
                normalized.Contains("context length") ||
                normalized.Contains("maximum context") ||
                normalized.Contains("too many tokens") ||
                normalized.Contains("token limit") ||
                normalized.Contains("prompt is too long") ||
                normalized.Contains("input is too long");
        }

        private void UpdateTokenEstimate()
        {
            // Provider usage is telemetry only, never the compaction authority:
            // opencode Go reports a caching-inclusive count (~1M cachedInput on a
            // 15K-token request), so folding it in via Math.Max pinned the
            // estimate at 900K+ and forced compaction every round. The local
            // history estimate is the compaction authority.
            m_EstimatedTokens = new AgentContextBudget(
                m_ClientFactory.GetProfile()).Estimate(m_History);
        }

        private async Task MaybeCompactAsync(
            CancellationToken cancellationToken,
            bool forceAggressive = false)
        {
            AgentModelProfile profile = m_ClientFactory.GetProfile();
            var budget = new AgentContextBudget(profile);
            if (!budget.ShouldCompact(m_EstimatedTokens, forceAggressive))
            {
                return;
            }

            List<ChatMessage> oldMessages;
            List<ChatMessage> keptMessages;
            lock (m_Lock)
            {
                AgentContextBudget.CompactionSlice slice = budget.CreateSlice(m_History, forceAggressive);
                if (slice == null)
                {
                    return;
                }
                oldMessages = slice.OldMessages;
                keptMessages = slice.KeptMessages;
            }
            IChatClient client = m_ClientFactory.GetClient();
            if (client == null)
            {
                return;
            }

            var summaryInput = AgentContextBudget.BuildSummaryInput(oldMessages, CompactionTaskPrompt);
            ModelSettings settings = ReadModel();
            ChatOptions options = ModelRequest.Options(settings.Model, profile, m_ToolSurface.Build(profile));
            ModelCall modelCall = ModelCall.Begin(m_Record, "compact", 1, settings, summaryInput, options);
            try
            {
                // NOTE: unary GetResponseAsync returns zero messages through the
                // Responses-backed client, so the summary must use the same
                // streaming path as the main loop.
                var updates = new List<ChatResponseUpdate>();
                await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                    summaryInput,
                    options,
                    cancellationToken))
                {
                    updates.Add(update);
                }
                ChatResponse response = updates.ToChatResponse();
                modelCall.Completed(response, CollectReasoning(updates), new FunctionCallContent[0]);

                string summary = (response.Text ?? "").Trim();
                if (!AgentContextBudget.IsUsableSummary(summary))
                {
                    m_Record.Error(
                        "compact",
                        "rejected unusable summary: " + AgentContextBudget.Truncate(summary, 400));
                    Emit(new SessionUpdate
                    {
                        Kind = "error",
                        Text = "Compaction summary rejected (tool markup or empty); skipping this compaction",
                    });
                    return;
                }

                lock (m_Lock)
                {
                    int nowCount = m_History.Count;
                    int keepStart = AgentContextBudget.FindSafeKeepStart(m_History, keptMessages.Count);
                    if (keepStart < nowCount)
                    {
                        keptMessages = m_History.Skip(keepStart).ToList();
                    }
                    m_PromptAssembler.Rebuild(
                        m_History,
                        summary,
                        keptMessages);
                    m_PromptAssembler.Apply(m_History);
                }
                m_EstimatedTokens = budget.Estimate(m_History);

                m_Record.Compact(
                    (double)profile.CompactAtTokens / profile.ContextWindowTokens,
                    oldMessages.Count,
                    keptMessages.Count,
                    summary,
                    m_EstimatedTokens);
                Emit(new SessionUpdate
                {
                    Kind = "compact",
                    Text = "Context compacted (removed " + oldMessages.Count + " old messages)",
                });
            }
            catch (OperationCanceledException)
            {
                modelCall.Ended("cancelled");
                throw;
            }
            catch (Exception e)
            {
                modelCall.Ended("failed", e);
                m_Record.Error("compact", e.ToString());
                Emit(new SessionUpdate
                {
                    Kind = "error",
                    Text = "Compaction failed: " + SessionRecord.RedactSecrets(e.Message),
                });
            }
        }

        private static string TruncateForLog(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text ?? "";
            }
            return text.Substring(0, maxChars) + "…";
        }

        private static string CollectReasoning(List<ChatResponseUpdate> updates)
        {
            if (updates == null || updates.Count == 0)
            {
                return "";
            }
            var builder = new StringBuilder();
            foreach (ChatResponseUpdate update in updates)
            {
                foreach (AIContent content in update.Contents)
                {
                    if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    {
                        builder.Append(reasoning.Text);
                    }
                }
            }
            return builder.ToString().Trim();
        }

        private void EmitGeneration(
            string modelCall, ChatResponse response, List<FunctionCallContent> toolCalls, string reasoning, long elapsedMs)
        {
            JsonObject usage = AgentUsageJson.Serialize(response.Usage);
            AgentUsageJson.Accumulate(m_TurnUsage, m_TurnUsageCoverage, response.Usage);
            m_Record.Generation(
                modelCall,
                response.ModelId ?? ReadModel().Model,
                reasoning,
                ModelCallJson.ToolCalls(toolCalls),
                usage,
                elapsedMs);
        }

        private static string ToolCallNames(ChatMessage message)
        {
            var names = new List<string>();
            foreach (AIContent content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    names.Add(call.Name);
                }
            }
            return names.Count == 0 ? null : string.Join(",", names);
        }

        private void ArmGeneration(CancellationTokenSource generation)
        {
            lock (m_CancelGate)
            {
                m_GenerationCts = generation;
            }
        }

        private void DisarmGeneration(CancellationTokenSource generation)
        {
            lock (m_CancelGate)
            {
                if (ReferenceEquals(m_GenerationCts, generation))
                {
                    m_GenerationCts = null;
                }
                generation.Dispose();
            }
        }

        private void CancelGeneration()
        {
            lock (m_CancelGate)
            {
                m_GenerationCts?.Cancel();
            }
        }

        private void EmitPlan()
        {
            Emit(new SessionUpdate { Kind = "plan", Text = m_Plan.ToUiJson() });
        }

        private void Emit(SessionUpdate uiEvent)
        {
            if (uiEvent.Kind == "status")
            {
                Status = uiEvent.Status;
            }
            Updated?.Invoke(uiEvent);
        }

        public void Dispose()
        {
            if (m_Disposed)
            {
                return;
            }
            m_Disposed = true;
            m_LoopCts.Cancel();
            m_TurnCts?.Cancel();
            m_Conversations?.CancelAll();
            CancelGeneration();
            m_Record.Dispose();
            m_ClientFactory.Dispose();
        }

        private ModelSettings ReadModel()
        {
            return m_ReadModel();
        }

        private ModelSettings ChildModel()
        {
            ModelSettings settings = m_ReadModel();
            settings.ContinueWhenIdle = false;
            return settings;
        }

        private async Task<string> RunChildTurnAsync(string sessionId, string message, CancellationToken cancellationToken)
        {
            var child = new AgentRuntime(
                m_Tools, m_SystemPrompt, ChildModel, m_Record.Conversation(sessionId), m_ChatClient, m_Warn, enableTasks: false);
            var relay = new ChildToolRelay();
            try
            {
                SessionTurnResult result = await SessionTurn.RunAsync(
                    child,
                    message,
                    update => relay.On(this, sessionId, update),
                    cancellationToken).ConfigureAwait(false);
                return result.Reply ?? "";
            }
            finally
            {
                child.Dispose();
            }
        }

        private sealed class ChildToolRelay
        {
            private string m_OpenCall;
            private int m_OpenCount;

            public void On(AgentRuntime runtime, string sessionId, SessionUpdate update)
            {
                if (!string.IsNullOrEmpty(update.CallId))
                {
                    runtime.Emit(update);
                    return;
                }
                if (m_OpenCall == null)
                {
                    m_OpenCount++;
                    m_OpenCall = "child:" + sessionId + ":" + m_OpenCount.ToString();
                    runtime.Emit(new SessionUpdate
                    {
                        Kind = "tool",
                        CallId = m_OpenCall,
                        Tool = update.Tool,
                        Text = update.Text,
                        Status = AgentStatus.Working,
                    });
                    return;
                }
                runtime.Emit(new SessionUpdate
                {
                    Kind = "tool",
                    CallId = m_OpenCall,
                    Tool = update.Tool,
                    Result = update.Text,
                    Status = update.Status == AgentStatus.Error ? AgentStatus.Error : AgentStatus.Idle,
                });
                m_OpenCall = null;
            }
        }

        private sealed class ModelRound
        {
            public string Text = "";
            public List<FunctionCallContent> ToolCalls = new List<FunctionCallContent>();
            public UsageDetails Usage;
            public bool IsError;
            public bool IsPlayerMessage;

            public static ModelRound Error(string message)
            {
                return new ModelRound { IsError = true, Text = message };
            }

            public static ModelRound PlayerMessage()
            {
                return new ModelRound { IsPlayerMessage = true };
            }
        }
    }
}
