using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRuntime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

public sealed class HarnessSessionTests
{
    private static readonly string Records = Path.Combine(Path.GetTempPath(), "airimayor-harness-" + Guid.NewGuid().ToString("N"));
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        { if (DateTime.UtcNow > deadline) throw new TimeoutException("scenario did not settle"); await Task.Delay(10); }
    }
    private static IReadOnlyList<ChatResponseUpdate> Text(string text) => new[] { new ChatResponseUpdate(ChatRole.Assistant, text) };
    private static IReadOnlyList<ChatResponseUpdate> Calls(params (string id, string name, IDictionary<string, object> args)[] calls) =>
        new[] { new ChatResponseUpdate(ChatRole.Assistant, calls.Select(call =>
            (AIContent)new FunctionCallContent(call.id, call.name, call.args)).ToList()) };
    private static Dictionary<string, object> Empty() => new();
    private static Task<ClientSession> Open(Client client, Tools tools = null, int window = 128000, int output = 16384)
    {
        var session = new ClientSession(() => client, tools ?? new Tools(), "City mayor instructions", window, output, true, new SessionRecordOptions { Root = Records });
        session.Updated += update => { if (update.Kind == "error") Console.WriteLine("EXPECTED/TEST ERROR: " + update.Text); };
        return Task.FromResult(session);
    }
    private static async Task Prompt(ClientSession session, string text)
    {
        int turns = 0;
        void Observe(SessionUpdate update) { if (update.Kind == "turn") Interlocked.Increment(ref turns); }
        session.Updated += Observe;
        session.Prompt(text);
        await Wait(() => turns > 0);
        session.Updated -= Observe;
    }
    [Fact]
    public async Task Stop_leaves_pending_steering_for_the_next_prompt_in_arrival_order()
    {
        var entered = new TaskCompletionSource();
        var client = new Client(async (n, _, _, ct) =>
        {
            if (n == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return Text("resumed");
        });
        using var session = await Open(client);
        session.Prompt("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Prompt("steer");
        session.Cancel();
        await Wait(() => session.Status == AgentStatus.Idle);
        await Task.Delay(100);
        Require(client.Calls == 1, "pending steering restarted the stopped run");
        await Prompt(session, "next");
        var sent = new[] { "first", "steer", "next" };
        Assert.Equal(sent, client.LastInput.Where(item => item.Role == ChatRole.User && sent.Contains(item.Text)).Select(item => item.Text));
        await session.CloseAsync();
    }

    [Fact]
    public async Task Missing_connection_settings_fail_the_prompt_and_the_record_closes()
    {
        var connection = new ModelConnection { Endpoint = "https://example.invalid/v1" };
        using var session = new ClientSession(connection.Open, new Tools(), "City mayor instructions",
            128000, 16384, true, new SessionRecordOptions { Root = Records });
        var turn = await SessionTurn.RunAsync(session, "hello", null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(turn.Failed);
        Assert.Contains("API key", turn.Error);
        await session.CloseAsync();
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(Path.Combine(session.Record.Directory, "session.json")))["ended"]);
    }

    [Fact]
    public async Task City_tool_failure_keeps_its_error_row_and_arguments()
    {
        var tools = new Tools { Run = _ => throw new InvalidOperationException("city tool failure") };
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ?
            Calls(("failed", "city_read", new Dictionary<string, object> { ["district"] = "river" })) : Text("reported")));
        using var session = await Open(client, tools);
        await Prompt(session, "inspect");
        var row = JsonNode.Parse(session.ChatStateJson())["messages"].AsArray().Single(item => (string)item["role"] == "tool");
        Assert.Equal("Error", (string)row["status"]);
        Assert.Contains("river", (string)row["arguments"]);
        Assert.Contains("city tool failure", (string)row["text"]);
        await session.CloseAsync();
    }
    [Fact]
    public async Task ToolsAndRecords()
    {
        var tools = new Tools();
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(
            ("one", "city_read", Empty()), ("two", "city_read", Empty())) : Text("done")));
        using var session = await Open(client, tools);
        await Prompt(session, "inspect");
        Require(tools.Count == 2 && tools.MaxActive == 1, "tools were not sequential");
        Require(client.Catalog.All(name => name == "city_read" || name.StartsWith("background_agents_") || name.StartsWith("todos_")), "unexpected tools");
        await session.CloseAsync();
        var timeline = File.ReadAllText(Path.Combine(session.Record.Directory, "timeline.jsonl"));
        Require(timeline.Contains("evidence") && timeline.Contains("queuedMs") && timeline.Contains("applied"), "city evidence/anchors lost");
        Require(Directory.GetFiles(session.Record.Directory, "probe.txt", SearchOption.AllDirectories).Length == 2, "host tool files missing");
        Require(client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>().All(item => item.Result as string == "{}"), "host-only metadata leaked into model tool result");
        Require(new FileInfo(Path.Combine(session.Record.Directory, "model-calls.jsonl")).Length == 0, "custom model capture still active");
        Require(session.ChatStateJson().Contains("city_read"), "city UI tool name lost");
    }
    [Fact]
    public async Task Todos()
    {
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(("todo", "todos_add",
            new Dictionary<string, object> { ["todos"] = new[] { new { title = "keep lights on", description = "city commitment" } } })) : Text("noted")));
        using var session = await Open(client); await Prompt(session, "plan"); await Prompt(session, "continue");
        Require(session.ChatStateJson().Contains("keep lights on"), "todo missing");
        Require((await session.Head.Agent.GetService<TodoProvider>().GetAllTodosAsync(session.Head.Session)).Count == 1, "native state missing");
        await session.CloseAsync();
    }
    [Fact]
    public async Task SteeringTool()
    {
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var tools = new Tools { Run = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return "ok"; } };
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(("hold", "city_read", Empty())) : Text("steered")));
        using var session = await Open(client, tools); session.Prompt("start"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Prompt("steer"); await Task.Delay(50);
        Require(!tools.Cancelled && client.Calls == 1, "steering cancelled or bypassed tool");
        release.SetResult(); await Wait(() => client.Calls >= 2 && session.Status == AgentStatus.Idle);
        Require(client.LastInput.Any(item => item.Text == "steer"), "steering missing"); await session.CloseAsync();
    }
    [Fact]
    public async Task SteeringGeneration()
    {
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); bool cancelled = false;
        var client = new Client(async (n, _, _, ct) =>
        {
            if (n == 1) { entered.TrySetResult(); try { await release.Task.WaitAsync(ct); } catch (OperationCanceledException) { cancelled = true; throw; } }
            return Text(n == 1 ? "old reply" : "new reply");
        });
        using var session = await Open(client); session.Prompt("first"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Prompt("steer"); await Task.Delay(50); Require(client.Calls == 1 && !cancelled, "native steering interrupted generation");
        release.SetResult(); await Wait(() => client.Calls == 2 && session.Status == AgentStatus.Idle);
        Require(client.LastInput.Any(item => item.Text == "steer"), "queued steering lost"); await session.CloseAsync();
    }
    [Fact]
    public async Task StopTool()
    {
        var entered = new TaskCompletionSource();
        var tools = new Tools { Run = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return "never"; } };
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(
            ("cancelled", "city_read", Empty()), ("not-started", "city_read", Empty())) : Text("resumed")));
        using var session = await Open(client, tools); session.Prompt("first"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Cancel(); await Wait(() => session.Status == AgentStatus.Idle); Require(tools.Cancelled, "tool not cancelled");
        await Prompt(session, "next"); Require(tools.Count == 1, "remaining batch executed"); await session.CloseAsync();
    }
    [Fact]
    public async Task StopGeneration()
    {
        var entered = new TaskCompletionSource();
        var client = new Client(async (n, _, _, ct) =>
        { if (n == 1) return Calls(("completed", "city_read", Empty())); if (n == 2) { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); } return Text("next"); });
        using var session = await Open(client); session.Prompt("first"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Cancel(); await Wait(() => session.Status == AgentStatus.Idle); await Prompt(session, "next");
        var result = client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Single(item => item.CallId == "completed");
        Require(result.Result as string == "{}", "completed result replaced by interruption"); await session.CloseAsync();
    }
    [Fact]
    public async Task FirstCancellation()
    {
        var entered = new TaskCompletionSource();
        var client = new Client(async (n, _, _, ct) => { if (n == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); } return Text("next"); });
        using var session = await Open(client); session.Prompt("original commitment"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Cancel(); await Wait(() => session.Status == AgentStatus.Idle); await Prompt(session, "next");
        Require(client.LastInput.Any(item => item.Text == "original commitment"), "first prompt lost"); await session.CloseAsync();
    }
    [Fact]
    public async Task Images()
    {
        var tools = new Tools { Image = new byte[] { 137, 80, 78, 71 } };
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(("image-1", "city_read", Empty()), ("image-2", "city_read", Empty())) : Text("seen")));
        using var session = await Open(client, tools); await Prompt(session, "look"); await Prompt(session, "again");
        var results = client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>().ToList();
        Require(results.Count == 2 && results.All(item => ((IEnumerable<AIContent>)item.Result).OfType<DataContent>().Count() == 1), "native result image lost");
        Require(client.LastInput.Where(item => item.Role == ChatRole.User).All(item => !item.Contents.OfType<DataContent>().Any()), "synthetic image messages remain"); await session.CloseAsync();
    }
    [Fact]
    public async Task Compaction()
    {
        var tools = new Tools { Run = _ => Task.FromResult(new string('x', 18000)) };
        var client = new Client((n, _, _, _) => Task.FromResult(n <= 10 ? Calls(("read-" + n, "city_read", Empty())) : Text("done")));
        using var session = await Open(client, tools, 16000, 1000); await Prompt(session, "long task");
        Require(tools.Count == 10, "tool loop ended early");
        int largest = client.Inputs.Max(input => input.Sum(item => item.Text?.Length ?? 0) + input.SelectMany(item => item.Contents).OfType<FunctionResultContent>()
            .Sum(item => JsonSerializer.Serialize(item.Result).Length));
        var history = session.Head.History.GetMessages(session.Head.Session).ToList();
        Require(history.Count < 22 || client.Inputs.Skip(4).Any(input => input.SelectMany(item => item.Contents).OfType<FunctionResultContent>()
            .Any(item => item.Result is string)), "compaction did not shrink anything");
        Console.WriteLine($"  compaction: largest input chars={largest}; persisted messages={history.Count}"); await session.CloseAsync();
    }
    [Fact]
    public async Task Background()
    {
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); int parent = 0, child = 0;
        var client = new Client(async (_, _, options, ct) =>
        {
            if (!options.Tools.Any(tool => tool.Name == "background_agents_start_task"))
            { Interlocked.Increment(ref child); entered.TrySetResult(); await release.Task.WaitAsync(ct); return Text("inspection report"); }
            int step = Interlocked.Increment(ref parent);
            return step == 1 ? Calls(("spawn", "background_agents_start_task", new Dictionary<string, object>
                { ["agentName"] = "inspector", ["input"] = "inspect city", ["description"] = "inspection" })) :
                step == 3 ? Calls(("wait", "background_agents_wait_for_first_completion", new Dictionary<string, object> { ["taskIds"] = new[] { 1 } })) :
                step == 4 ? Calls(("results", "background_agents_get_task_results", new Dictionary<string, object> { ["taskId"] = 1 })) : Text("parent done");
        });
        using var session = await Open(client); session.Prompt("delegate"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Wait(() => parent == 2 && session.Status == AgentStatus.Idle); session.Cancel(); release.SetResult(); await Wait(() => child == 1); await Task.Delay(150);
        Require(parent == 2, "native child auto-woke root"); await Prompt(session, "get report");
        Require(parent == 5 && client.Inputs.Any(input => input.SelectMany(item => item.Contents).OfType<FunctionResultContent>()
            .Any(result => JsonSerializer.Serialize(result.Result).Contains("inspection report"))), "native results missing"); await session.CloseAsync();
    }
    [Fact]
    public async Task CloseBackground()
    {
        var entered = new TaskCompletionSource(); var cancelled = new TaskCompletionSource(); int parent = 0;
        var client = new Client(async (_, _, options, ct) =>
        {
            if (!options.Tools.Any(tool => tool.Name == "background_agents_start_task"))
            { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled.SetResult(); throw; } }
            return Interlocked.Increment(ref parent) == 1 ? Calls(("spawn", "background_agents_start_task", new Dictionary<string, object>
                { ["agentName"] = "inspector", ["input"] = "inspect", ["description"] = "inspection" })) : Text("done");
        });
        using var session = await Open(client); session.Prompt("delegate"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); Require(cancelled.Task.IsCompleted, "native child was not released");
    }
    [Fact]
    public async Task Projection()
    {
        var client = new Client((_, _, _, _) => Task.FromResult(Text("ok"))); using var session = await Open(client);
        session.Prompt("model city coordinates", "here", "[{\"label\":\"square\"}]"); await Wait(() => client.Calls == 1 && session.Status == AgentStatus.Idle);
        Require(session.ChatStateJson().Contains("square") && client.LastInput.Any(item => item.Text == "model city coordinates"), "places projection lost");
        Require(!client.LastInput.Any(item => (item.Text ?? "").Contains("square")), "places leaked into model text");
        for (int i = 0; i < 110; i++) await Prompt(session, "next " + i);
        var state = JsonNode.Parse(session.ChatStateJson()); Require(state["messages"].AsArray().Count <= 200, "UI history unbounded");
        Require(!string.IsNullOrEmpty((string)state["session"]), "UI session missing"); await session.CloseAsync();
    }
    [Fact]
    public async Task NativeResultCancellation()
    {
        var entered = new TaskCompletionSource(); var childEntered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        bool childCancelled = false; int parent = 0;
        var client = new Client(async (_, _, options, ct) =>
        {
            if (!options.Tools.Any(tool => tool.Name == "background_agents_start_task"))
            { childEntered.TrySetResult(); try { await release.Task.WaitAsync(ct); } catch (OperationCanceledException) { childCancelled = true; throw; } return Text("child done"); }
            int step = Interlocked.Increment(ref parent);
            if (step == 1) return Calls(("spawn", "background_agents_start_task", new Dictionary<string, object>
                { ["agentName"] = "inspector", ["input"] = "inspect city", ["description"] = "inspection" }));
            if (step == 2) { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return Text("continued");
        });
        using var session = await Open(client); session.Prompt("delegate");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Cancel(); await Wait(() => session.Status == AgentStatus.Idle); Require(!childCancelled, "root Stop cancelled native child");
        await Prompt(session, "continue");
        var spawn = client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Single(item => item.CallId == "spawn");
        Require(!JsonSerializer.Serialize(spawn.Result).Contains("interrupted"), "native tool result replaced by interruption");
        release.SetResult(); await session.CloseAsync();
    }
    [Fact]
    public async Task ChildRecovery()
    {
        int parent = 0, child = 0; List<ChatMessage> resumed = null;
        var failed = new TaskCompletionSource();
        var client = new Client((_, input, options, _) =>
        {
            if (!options.Tools.Any(tool => tool.Name == "background_agents_start_task"))
            {
                int step = Interlocked.Increment(ref child);
                if (step == 1) return Task.FromResult(Calls(("child-city", "city_read", Empty())));
                if (step == 2) { failed.SetResult(); throw new InvalidOperationException("child model failure"); }
                resumed = input; return Task.FromResult(Text("child recovered"));
            }
            int root = Interlocked.Increment(ref parent);
            return Task.FromResult(root == 1 ? Calls(("spawn", "background_agents_start_task", new Dictionary<string, object>
                { ["agentName"] = "inspector", ["input"] = "inspect", ["description"] = "inspection" })) :
                root == 3 ? Calls(("wait-failure", "background_agents_wait_for_first_completion", new Dictionary<string, object> { ["taskIds"] = new[] { 1 } })) :
                root == 4 ? Calls(("resume", "background_agents_continue_task", new Dictionary<string, object> { ["taskId"] = 1, ["text"] = "continue" })) :
                root == 5 ? Calls(("wait-success", "background_agents_wait_for_first_completion", new Dictionary<string, object> { ["taskIds"] = new[] { 1 } })) : Text("done"));
        });
        using var session = await Open(client); session.Prompt("delegate"); await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Wait(() => parent == 2 && session.Status == AgentStatus.Idle); await Prompt(session, "resume child");
        Require(child == 3 && resumed != null, "native child did not continue");
        var result = resumed.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Single(item => item.CallId == "child-city");
        Require(result.Result as string == "{}", "child completed city result lost"); await session.CloseAsync();
    }
    [Fact]
    public async Task NativeError()
    {
        var client = new Client((_, _, _, _) => throw new InvalidOperationException("context_length_exceeded"));
        using var session = await Open(client); await Prompt(session, "oversized");
        Require(client.Calls == 1 && session.ChatStateJson().Contains("context_length_exceeded"), "bespoke retry still active"); await session.CloseAsync();
    }
    [Fact]
    public async Task WireImage()
    {
        var handler = new CaptureHttp();
        var options = new OpenAI.OpenAIClientOptions { Endpoint = new Uri("http://127.0.0.1/"),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new System.Net.Http.HttpClient(handler)) };
        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("fake"), options).GetResponsesClient().AsIChatClient("script");
        var input = new[] { new ChatMessage(ChatRole.User, "look"),
            new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("pic", "city_read", Empty()) }),
            new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("pic", new AIContent[] {
                new TextContent("city view"), new DataContent(new byte[] { 137, 80, 78, 71 }, "image/png") }) }) };
        await client.GetResponseAsync(input);
        var json = JsonNode.Parse(handler.Body); var output = json["input"].AsArray().Single(item => (string)item["type"] == "function_call_output");
        Require(output["output"].AsArray().Any(item => (string)item["type"] == "input_image") &&
            output["output"].AsArray().Any(item => (string)item["type"] == "input_text"), "SDK serialized tool image as opaque JSON");

    }
    private sealed class CaptureHttp : System.Net.Http.HttpMessageHandler
    {
        public string Body;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(
                "{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"script\",\"output\":[]}", System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Client : IChatClient
    {
        private readonly Func<int, List<ChatMessage>, ChatOptions, CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>> Handler;
        public int Calls, Summaries;
        public List<ChatMessage> LastInput = new(); public List<List<ChatMessage>> Inputs = new(); public List<string> Catalog = new();
        public Client(Func<int, List<ChatMessage>, ChatOptions, CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>> handler) => Handler = handler;
        public object GetService(Type type, object key = null) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default) => GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken: cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var input = messages.ToList(); var pending = new HashSet<string>();
            foreach (var message in input)
            {
                if (message.Role == ChatRole.User) Require(pending.Count == 0, "user image/message split tool result batch");
                foreach (var content in message.Contents)
                    if (content is FunctionCallContent call) pending.Add(call.CallId);
                    else if (content is FunctionResultContent result) Require(pending.Remove(result.CallId), "orphan/duplicate result " + result.CallId);
            }
            Require(pending.Count == 0, "unpaired function calls: " + string.Join(",", pending));
            IReadOnlyList<ChatResponseUpdate> response;
            if (options?.Tools == null) { Interlocked.Increment(ref Summaries); response = Text("Summary: keep city commitments and tool outcomes."); }
            else
            {
                Require((options.Instructions ?? "").Contains("City mayor instructions") || input.Any(message =>
                    message.Role == ChatRole.System && (message.Text ?? "").Contains("City mayor instructions")), "system prompt missing");
                LastInput = input; lock (Inputs) Inputs.Add(input); Catalog = options.Tools.Select(tool => tool.Name).ToList();
                response = await Handler(Interlocked.Increment(ref Calls), input, options, cancellationToken);
            }
            foreach (var update in response) { cancellationToken.ThrowIfCancellationRequested(); yield return update; await Task.Yield(); }
        }
    }
    private sealed class Tools : IAgentTools
    {
        public Func<CancellationToken, Task<string>> Run = _ => Task.FromResult("{}");
        public int Count, Active, MaxActive; public bool Cancelled; public byte[] Image;
        public IReadOnlyList<AgentToolDeclaration> List(bool vision) => new[] { new AgentToolDeclaration
            { Name = "city_read", Description = "Read city facts", ParametersJson = "{\"type\":\"object\",\"properties\":{}}" } };
        public async Task<AgentToolResult> InvokeAsync(string name, string arguments, CancellationToken ct)
        {
            Count++; int active = Interlocked.Increment(ref Active); MaxActive = Math.Max(MaxActive, active);
            try { return new AgentToolResult { Success = true, Text = await Run(ct), ImagePng = Image,
                Applied = new SimulationStamp(1, "probe", 0), QueuedMs = 1, Evidence = new JsonObject { ["probe"] = true },
                Files = new Dictionary<string, byte[]> { ["probe.txt"] = System.Text.Encoding.UTF8.GetBytes("host evidence file") } }; }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
