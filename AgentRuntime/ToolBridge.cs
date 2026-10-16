using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRuntime
{
    /// <summary>
    /// Loopback HTTP in front of <see cref="IAgentTools"/>. The stdio MCP
    /// process the external agent spawns calls this; it never owns city code.
    /// </summary>
    public sealed class ToolBridge : IDisposable
    {
        private readonly IAgentTools m_Tools;
        private readonly Func<bool> m_VisionAvailable;
        private readonly string m_Instructions;
        private readonly ConversationBook m_Conversations;
        private readonly ToolPreviewLatch m_Previews;
        private readonly SessionRecord m_Record;
        private readonly string m_Token;
        private readonly string m_ChildToken;
        private readonly HttpListener m_Listener;
        private readonly CancellationTokenSource m_Stop = new CancellationTokenSource();
        private readonly object m_CallsGate = new object();
        private readonly Dictionary<string, CancellationTokenSource> m_Calls = new Dictionary<string, CancellationTokenSource>();
        private readonly Task m_Loop;

        private ToolBridge(
            IAgentTools tools,
            Func<bool> visionAvailable,
            string instructions,
            ConversationBook conversations,
            ToolPreviewLatch previews,
            SessionRecord record,
            string token,
            string childToken,
            HttpListener listener)
        {
            m_Tools = tools;
            m_VisionAvailable = visionAvailable ?? (() => false);
            m_Instructions = instructions ?? "";
            m_Conversations = conversations;
            m_Previews = previews;
            m_Record = record;
            m_Token = token;
            m_ChildToken = childToken;
            m_Listener = listener;
            m_Loop = Task.Run(ListenAsync);
        }

        public string Token => m_Token;

        public string ChildToken => m_ChildToken;

        public int Port { get; private set; }

        public string BaseUrl => "http://127.0.0.1:" + Port.ToString() + "/";

        public static ToolBridge Start(
            IAgentTools tools,
            Func<bool> visionAvailable,
            string instructions,
            ConversationBook conversations,
            ToolPreviewLatch previews = null,
            SessionRecord record = null)
        {
            if (tools == null)
            {
                throw new ArgumentNullException(nameof(tools));
            }

            var listener = new HttpListener();
            int port = BindLoopback(listener);
            string token = NewToken();
            string childToken = NewToken();
            var bridge = new ToolBridge(tools, visionAvailable, instructions, conversations, previews, record, token, childToken, listener)
            {
                Port = port,
            };
            return bridge;
        }

        public void Dispose()
        {
            m_Stop.Cancel();
            try
            {
                m_Listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }
            try
            {
                m_Loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            m_Listener.Close();
            m_Stop.Dispose();
        }

        private static int BindLoopback(HttpListener listener)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int port = FreePort();
                listener.Prefixes.Clear();
                listener.Prefixes.Add("http://127.0.0.1:" + port.ToString() + "/");
                try
                {
                    listener.Start();
                    return port;
                }
                catch (HttpListenerException)
                {
                }
            }
            throw new InvalidOperationException("Could not bind the city tool bridge to loopback.");
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static string NewToken()
        {
            byte[] bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private async Task ListenAsync()
        {
            while (!m_Stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await m_Listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (m_Stop.IsCancellationRequested)
                    {
                        return;
                    }
                    continue;
                }
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                Caller caller = Identify(context.Request);
                if (caller == Caller.None)
                {
                    context.Response.StatusCode = 401;
                    return;
                }
                string path = context.Request.Url?.AbsolutePath ?? "";
                if (context.Request.HttpMethod == "GET" && path == "/tools")
                {
                    await WriteJsonAsync(context, 200, ToolsJson(caller == Caller.Root)).ConfigureAwait(false);
                    return;
                }
                // HttpListener does not report that the caller dropped the connection.
                // The MCP process posts here when its tool call is abandoned.
                if (context.Request.HttpMethod == "POST" && path == "/cancel")
                {
                    CancelCall(context.Request.QueryString["id"]);
                    await WriteJsonAsync(context, 200, "{}").ConfigureAwait(false);
                    return;
                }
                if (context.Request.HttpMethod == "POST" && path == "/invoke")
                {
                    string body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
                    string callId = context.Request.Headers["X-Call-Id"];
                    CancellationTokenSource call = BeginCall(callId);
                    try
                    {
                        string json = await InvokeJsonAsync(body, callId, caller == Caller.Root, call.Token).ConfigureAwait(false);
                        await WriteJsonAsync(context, 200, json).ConfigureAwait(false);
                    }
                    finally
                    {
                        EndCall(callId, call);
                    }
                    return;
                }
                context.Response.StatusCode = 404;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                try
                {
                    await WriteJsonAsync(context, 500, JsonSerializer.Serialize(new { error = e.Message })).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        private Caller Identify(HttpListenerRequest request)
        {
            string header = request.Headers["Authorization"];
            if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                return Caller.None;
            }
            string presented = header.Substring("Bearer ".Length).Trim();
            if (FixedEquals(presented, m_Token))
            {
                return Caller.Root;
            }
            if (FixedEquals(presented, m_ChildToken))
            {
                return Caller.Child;
            }
            return Caller.None;
        }

        private static bool FixedEquals(string presented, string expected)
        {
            byte[] left = Encoding.UTF8.GetBytes(presented ?? "");
            byte[] right = Encoding.UTF8.GetBytes(expected ?? "");
            return left.Length == right.Length && FixedTimeEquals(left, right);
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }
            return diff == 0;
        }

        private string ToolsJson(bool isRoot)
        {
            var tools = new JsonArray();
            foreach (AgentToolDeclaration tool in m_Tools.List(m_VisionAvailable()))
            {
                tools.Add(ToolJson(tool));
            }
            if (isRoot && m_Conversations != null)
            {
                foreach (AgentToolDeclaration tool in m_Conversations.List())
                {
                    tools.Add(ToolJson(tool));
                }
            }
            return new JsonObject
            {
                ["instructions"] = m_Instructions,
                ["tools"] = tools,
            }.ToJsonString();
        }

        private async Task<string> InvokeJsonAsync(string body, string callId, bool isRoot, CancellationToken cancellationToken)
        {
            string name = "";
            string arguments = "{}";
            if (!string.IsNullOrWhiteSpace(body))
            {
                using (JsonDocument document = JsonDocument.Parse(body))
                {
                    JsonElement root = document.RootElement;
                    if (root.TryGetProperty("name", out JsonElement nameElement))
                    {
                        name = nameElement.GetString() ?? "";
                    }
                    if (root.TryGetProperty("argumentsJson", out JsonElement argumentsElement) &&
                        argumentsElement.ValueKind == JsonValueKind.String)
                    {
                        arguments = argumentsElement.GetString() ?? "{}";
                    }
                }
            }
            if (m_Conversations != null && string.Equals(name, ConversationBook.TaskName, StringComparison.Ordinal))
            {
                if (!isRoot)
                {
                    return new JsonObject
                    {
                        ["success"] = false,
                        ["text"] = "This conversation cannot open another one.",
                        ["imagePngBase64"] = null,
                    }.ToJsonString();
                }
                AgentToolResult session = await RecordedAsync(callId, name, arguments, isRoot,
                    () => m_Conversations.InvokeAsync(name, arguments, cancellationToken)).ConfigureAwait(false);
                return new JsonObject
                {
                    ["success"] = session.Success,
                    ["text"] = session.Text ?? "",
                    ["imagePngBase64"] = null,
                }.ToJsonString();
            }
            AgentToolResult result = await RecordedAsync(callId, name, arguments, isRoot,
                () => m_Tools.InvokeAsync(name, arguments, cancellationToken)).ConfigureAwait(false);
            m_Previews?.Offer(name, arguments, result.PreviewJpeg);
            string image = result.ImagePng == null || result.ImagePng.Length == 0
                ? null
                : Convert.ToBase64String(result.ImagePng);
            return new JsonObject
            {
                ["success"] = result.Success,
                ["text"] = result.Text ?? "",
                ["imagePngBase64"] = image,
            }.ToJsonString();
        }

        /// <summary>
        /// The external mayor's city tool calls go into the session record with
        /// the same events and anchors as the built-in loop. The call id is the
        /// tool server's, not the agent's tool row id.
        /// </summary>
        private async Task<AgentToolResult> RecordedAsync(
            string callId, string name, string arguments, bool isRoot, Func<Task<AgentToolResult>> invoke)
        {
            if (m_Record == null)
            {
                return await invoke().ConfigureAwait(false);
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            SimulationStamp submitted = m_Record.Clock();
            m_Record.FunctionStart(callId, name);
            AgentToolResult result;
            try
            {
                result = await invoke().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                m_Record.Function(callId, name, arguments,
                    new AgentToolResult { Success = false, Text = "tool call interrupted" },
                    timer.ElapsedMilliseconds,
                    new ToolAnchors { Submitted = submitted, Completed = m_Record.Clock() },
                    "interrupted");
                throw;
            }
            m_Record.Function(callId, name, arguments, result, timer.ElapsedMilliseconds,
                new ToolAnchors
                {
                    Submitted = submitted,
                    Applied = result.Applied,
                    Completed = m_Record.Clock(),
                    QueuedMs = result.QueuedMs,
                },
                result.Success ? null : result.Text,
                isRoot ? null : "conversation");
            return result;
        }

        private static JsonObject ToolJson(AgentToolDeclaration tool)
        {
            return new JsonObject
            {
                ["name"] = tool.Name ?? "",
                ["description"] = tool.Description ?? "",
                ["parametersJson"] = tool.ParametersJson ?? "{}",
            };
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, int status, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json ?? "");
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        private CancellationTokenSource BeginCall(string callId)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(m_Stop.Token);
            if (string.IsNullOrEmpty(callId))
            {
                return source;
            }
            lock (m_CallsGate)
            {
                if (m_Calls.TryGetValue(callId, out CancellationTokenSource early))
                {
                    source.Cancel();
                    m_Calls.Remove(callId);
                    early.Dispose();
                }
                m_Calls[callId] = source;
            }
            return source;
        }

        private void EndCall(string callId, CancellationTokenSource source)
        {
            if (!string.IsNullOrEmpty(callId))
            {
                lock (m_CallsGate)
                {
                    if (m_Calls.TryGetValue(callId, out CancellationTokenSource current) &&
                        ReferenceEquals(current, source))
                    {
                        m_Calls.Remove(callId);
                    }
                }
            }
            source.Dispose();
        }

        private void CancelCall(string callId)
        {
            if (string.IsNullOrEmpty(callId))
            {
                return;
            }
            lock (m_CallsGate)
            {
                if (m_Calls.TryGetValue(callId, out CancellationTokenSource source))
                {
                    source.Cancel();
                    return;
                }
                var early = new CancellationTokenSource();
                early.Cancel();
                m_Calls[callId] = early;
            }
        }

        private enum Caller
        {
            None,
            Root,
            Child,
        }
    }
}
