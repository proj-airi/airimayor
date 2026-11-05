using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentRuntime
{
    /// <summary>The built-in chat adapter. Harness owns model history, tools, plans and background tasks.</summary>
    public sealed class ClientSession : IAgentSession
    {
        private readonly SemaphoreSlim m_Gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource m_Lifetime = new CancellationTokenSource();
        private readonly Channel<bool> m_Wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        private readonly List<ChatMessage> m_CurrentInput = new List<ChatMessage>();
        private readonly ChatProjection m_View;
        private readonly CityFunctions m_Functions;
        private readonly Func<Task> m_Open;
        private readonly Task m_Loop;
        private IChatClient m_Client;
        private Task m_Ready;
        private CancellationTokenSource m_RunCancellation;
        private Task m_Active;

        internal NativeHead Head { get; private set; }
        public SessionRecord Record { get; }
        public AgentStatus Status => m_View.Status;
        public event Action<SessionUpdate> Updated
        {
            add { m_View.Updated += value; }
            remove { m_View.Updated -= value; }
        }

        public ClientSession(Func<IChatClient> openClient, IAgentTools tools, string instructions,
            int windowTokens, int outputTokens, bool vision, SessionRecordOptions recordOptions, Action<string> warn = null)
        {
            Record = new SessionRecord(Guid.NewGuid().ToString("N"), recordOptions, warn);
            Record.DeclareGap("Harness owns model history and compaction; full model requests, replies and per-round usage are not recorded");
            Record.DeclareGap("native background tasks share city-tool evidence here; separate child records and task transcripts are not captured");
            m_View = new ChatProjection { SessionId = Record.SessionId };
            m_Functions = new CityFunctions(tools, Record, m_View.Apply);
            m_Open = async () =>
            {
                m_Client = openClient();
                var logger = new HostDiagnostics(Record, warn);
                var child = new NativeHead(m_Client, m_Functions.List(vision), instructions,
                    windowTokens, outputTokens, logger, name: "inspector");
                Head = new NativeHead(m_Client, m_Functions.List(vision), instructions,
                    windowTokens, outputTokens, logger, new[] { child.Agent });
                await Head.OpenAsync(m_Lifetime.Token).ConfigureAwait(false);
            };
            m_Loop = Task.Run(LoopAsync);
        }

        public void Prompt(string text) => Prompt(text, text, null);

        public void Prompt(string modelText, string displayText, string placesJson)
        {
            if (m_Lifetime.IsCancellationRequested) throw new ObjectDisposedException(nameof(ClientSession));
            m_View.Apply(new SessionUpdate { Kind = "user", Text = displayText ?? modelText, Places = placesJson });
            Observe(AcceptAsync(new ChatMessage(ChatRole.User, modelText)
            {
                MessageId = Guid.NewGuid().ToString("N"),
            }));
        }

        private async Task AcceptAsync(ChatMessage input)
        {
            await m_Gate.WaitAsync(m_Lifetime.Token).ConfigureAwait(false);
            try
            {
                if (m_Ready == null) m_Ready = m_Open();
                await m_Ready.ConfigureAwait(false);
                await Head.Injection.EnqueueMessagesAsync(Head.Session, new[] { input }, m_Lifetime.Token).ConfigureAwait(false);
                if (m_Active != null && !m_Active.IsCompleted && !m_RunCancellation.IsCancellationRequested)
                    m_CurrentInput.Add(input);
                else
                    m_Wake.Writer.TryWrite(true);
            }
            finally { m_Gate.Release(); }
        }

        public void Cancel() => Observe(CancelAsync());

        private async Task CancelAsync()
        {
            await m_Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                while (m_Wake.Reader.TryRead(out _)) { }
                m_RunCancellation?.Cancel();
                if (m_Active == null)
                    m_View.Apply(new SessionUpdate { Kind = "status", Status = AgentStatus.Interrupted });
            }
            finally { m_Gate.Release(); }
        }

        public string ChatStateJson() => m_View.Json(m_Wake.Reader.Count);

        private async Task LoopAsync()
        {
            try
            {
                while (await m_Wake.Reader.WaitToReadAsync(m_Lifetime.Token).ConfigureAwait(false))
                {
                    await m_Gate.WaitAsync(m_Lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        if (!m_Wake.Reader.TryRead(out _)) continue;
                        var input = await Head.Injection.GetPendingMessagesAsync(Head.Session, m_Lifetime.Token).ConfigureAwait(false);
                        if (input.Count == 0) continue;
                        m_CurrentInput.Clear();
                        m_CurrentInput.AddRange(input);
                        m_RunCancellation = CancellationTokenSource.CreateLinkedTokenSource(m_Lifetime.Token);
                        m_Active = RunAsync(input, m_RunCancellation.Token);
                    }
                    finally { m_Gate.Release(); }
                    await m_Active.ConfigureAwait(false);
                    await m_Gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // A prompt can arrive after the SDK's final queue check. Stop leaves it for the next player prompt.
                        if (!m_RunCancellation.IsCancellationRequested &&
                            (await Head.Injection.GetPendingMessagesAsync(Head.Session).ConfigureAwait(false)).Count > 0)
                            m_Wake.Writer.TryWrite(true);
                        m_Active = null;
                        m_RunCancellation.Dispose();
                        m_RunCancellation = null;
                        m_CurrentInput.Clear();
                    }
                    finally { m_Gate.Release(); }
                }
            }
            catch (OperationCanceledException) when (m_Lifetime.IsCancellationRequested) { }
            finally
            {
                try
                {
                    if (Head != null) await Head.ReleaseAsync().ConfigureAwait(false);
                }
                finally
                {
                    m_Client?.Dispose();
                    Record.Dispose();
                }
            }
        }

        private async Task RunAsync(IReadOnlyList<ChatMessage> input, CancellationToken cancellationToken)
        {
            Record.TurnStart(Guid.NewGuid().ToString("N"), string.Join("\n", input.Select(item => item.Text)));
            m_View.Apply(new SessionUpdate { Kind = "status", Status = AgentStatus.Thinking });
            var calls = new Dictionary<string, FunctionCallContent>();
            try
            {
                await foreach (var update in Head.RunAsync(Array.Empty<ChatMessage>(), cancellationToken).ConfigureAwait(false))
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is TextContent text)
                            m_View.Apply(new SessionUpdate { Kind = "delta", Text = text.Text });
                        else if (content is FunctionCallContent call)
                        {
                            calls[call.CallId] = call;
                            if (!m_Functions.Contains(call.Name))
                                m_View.Apply(new SessionUpdate
                                {
                                    Kind = "tool", CallId = m_Functions.Id(Head.Session, call.CallId), Tool = call.Name,
                                    Text = JsonSerializer.Serialize(call.Arguments), Status = AgentStatus.Working,
                                });
                        }
                        else if (content is FunctionResultContent result && calls.TryGetValue(result.CallId, out var original)
                            && !m_Functions.Contains(original.Name))
                        {
                            m_View.Apply(new SessionUpdate
                            {
                                Kind = "tool", CallId = m_Functions.Id(Head.Session, result.CallId), Tool = original.Name,
                                Text = JsonSerializer.Serialize(original.Arguments), Result = ResultText(result.Result),
                                Status = result.Exception == null ? AgentStatus.Idle : AgentStatus.Error,
                            });
                        }
                    }
                }
            }
            catch (Exception error)
            {
                await PreserveInputsAsync().ConfigureAwait(false);
                if (error is OperationCanceledException)
                    m_View.Apply(new SessionUpdate { Kind = "status", Status = AgentStatus.Interrupted });
                else
                    ReportError("agent", error);
            }
            finally
            {
                var todos = await Head.Agent.GetService<TodoProvider>().GetAllTodosAsync(Head.Session).ConfigureAwait(false);
                m_View.Apply(new SessionUpdate
                {
                    Kind = "plan", Text = SessionPlan.Format(todos.Select(todo =>
                        (todo.Title, "medium", todo.IsComplete ? "completed" : "pending"))),
                });
                Record.TurnFinish(null);
                m_View.Apply(new SessionUpdate { Kind = "status", Status = AgentStatus.Idle });
                m_View.Apply(new SessionUpdate { Kind = "turn" });
            }
        }

        private async Task PreserveInputsAsync()
        {
            await m_Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var history = Head.History.GetMessages(Head.Session).ToList();
                var pending = await Head.Injection.GetPendingMessagesAsync(Head.Session).ConfigureAwait(false);
                foreach (var input in m_CurrentInput)
                    if (!history.Any(item => item.MessageId == input.MessageId) && !pending.Any(item => item.MessageId == input.MessageId))
                        history.Add(input);
                Head.History.SetMessages(Head.Session, history);
            }
            finally { m_Gate.Release(); }
        }

        private static string ResultText(object result) => result is IEnumerable<AIContent> content
            ? string.Join("\n", content.OfType<TextContent>().Select(item => item.Text))
            : result as string ?? JsonSerializer.Serialize(result);

        private void ReportError(string source, Exception error)
        {
            string text = SessionRecord.RedactSecrets(error.Message);
            Record.Error(source, text);
            m_View.Apply(new SessionUpdate { Kind = "error", Text = text });
        }

        private async void Observe(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (m_Lifetime.IsCancellationRequested) { }
            catch (Exception error) { ReportError("client", error); }
        }

        public void Dispose() => m_Lifetime.Cancel();
        public async Task CloseAsync()
        {
            Dispose();
            await m_Loop.ConfigureAwait(false);
        }
    }
}
