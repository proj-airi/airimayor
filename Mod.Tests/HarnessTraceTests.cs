using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using airimayor.Agent;
using Microsoft.Extensions.AI;
using Xunit;

public sealed class HarnessTraceTests
{
    [Fact]
    public async Task Native_request_is_recorded_before_model_start_and_keeps_output_catalog_and_usage()
    {
        ClientSession session = null;
        bool checkpointed = false;
        string reply = new string('r', 12000);
        async IAsyncEnumerable<ChatResponseUpdate> Respond(List<ChatMessage> _, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var update = Read(session).Single(record => (string)record["type"] == "span.update" && (string)record["track"] == "agents/mayor/llm");
            Assert.Contains("inspect the river", Resolve(session, update["attrs"]["gen_ai.input.messages"]).ToJsonString());
            Assert.Contains("city_read", Resolve(session, update["attrs"]["gen_ai.tool.definitions"]).ToJsonString());
            Assert.Contains("City mayor instructions", Resolve(session, update["attrs"]["gen_ai.system_instructions"]).ToJsonString());
            checkpointed = true;
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("exposed reasoning"),
                new TextContent(reply),
            }) { ResponseId = "response-1", ModelId = "model-1" };
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new UsageContent(new UsageDetails { InputTokenCount = 17, OutputTokenCount = 9, CachedInputTokenCount = 3, ReasoningTokenCount = 4 }),
            }) { FinishReason = ChatFinishReason.Stop };
        }

        using (session = Open(new Client(Respond)))
        {
            await SessionTurn.RunAsync(session, "inspect the river").WaitAsync(TimeSpan.FromSeconds(10));
            await session.CloseAsync();
            Assert.True(checkpointed);
            var records = Read(session);
            var model = Assert.Single(records, record => (string)record["type"] == "span.end" && (string)record["track"] == "agents/mayor/llm");
            var output = Resolve(session, model["attrs"]["gen_ai.output.messages"]).ToJsonString();
            Assert.Contains(reply, output);
            Assert.Contains("exposed reasoning", output);
            Assert.Equal(17, (int)model["attrs"]["gen_ai.usage.input_tokens"]);
            Assert.Equal(9, (int)model["attrs"]["gen_ai.usage.output_tokens"]);
            Assert.Equal(3, (int)model["attrs"]["gen_ai.usage.cache_read.input_tokens"]);
            Assert.Equal(4, (int)model["attrs"]["gen_ai.usage.reasoning.output_tokens"]);
            Assert.Single(records, record => (string)record["name"] == "llm.first_chunk");
            Assert.Single(records, record => (string)record["type"] == "span.begin" && (string)record["spanId"] == (string)model["spanId"]);
        }
    }

    [Fact]
    public async Task Cancellation_keeps_native_partial_output_and_marks_the_model_span_cancelled()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<ChatResponseUpdate> Respond(List<ChatMessage> _, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "kept partial reply");
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        using var session = Open(new Client(Respond));
        session.Prompt("inspect");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Cancel();
        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var model = Assert.Single(Read(session), record => (string)record["type"] == "span.end" && (string)record["track"] == "agents/mayor/llm");
        Assert.Equal("cancelled", (string)model["status"]);
        Assert.Contains("kept partial reply", Resolve(session, model["attrs"]["gen_ai.output.messages"]).ToJsonString());
    }

    [Fact]
    public async Task Child_city_work_keeps_its_session_and_origin_after_a_new_root_turn()
    {
        var childEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int root = 0, child = 0;
        async IAsyncEnumerable<ChatResponseUpdate> Respond(List<ChatMessage> _, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (options.Tools.Any(tool => tool.Name == "background_agents_start_task"))
            {
                if (Interlocked.Increment(ref root) == 1)
                    yield return Call("spawn", "background_agents_start_task", new Dictionary<string, object>
                    {
                        ["agentName"] = "inspector", ["input"] = "inspect the river", ["description"] = "river inspection",
                    });
                else
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "root finished");
            }
            else if (Interlocked.Increment(ref child) == 1)
            {
                childEntered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                yield return Call("child-read", "city_read", new Dictionary<string, object>());
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "child finished");
                childFinished.TrySetResult();
            }
        }

        using var session = Open(new Client(Respond));
        await SessionTurn.RunAsync(session, "delegate").WaitAsync(TimeSpan.FromSeconds(10));
        await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SessionTurn.RunAsync(session, "new root commitment").WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        await childFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.CloseAsync();
        var records = Read(session);
        var roots = records.Where(record => (string)record["type"] == "span.begin" && (string)record["name"] == "invoke_agent mayor").ToList();
        Assert.Equal(2, roots.Count);
        Assert.Equal((string)roots[0]["ids"]["agentSession"], (string)roots[1]["ids"]["agentSession"]);
        Assert.NotEqual((string)roots[0]["ids"]["run"], (string)roots[1]["ids"]["run"]);
        var inspector = Assert.Single(records, record => (string)record["type"] == "span.begin" && (string)record["track"] == "agents/inspector/turn");
        Assert.NotEqual((string)roots[0]["ids"]["agentSession"], (string)inspector["ids"]["agentSession"]);
        Assert.Null(inspector["ids"]["turn"]);
        Assert.Equal((string)roots[0]["ids"]["turn"], (string)inspector["ids"]["originTurn"]);
        var tool = Assert.Single(records, record => (string)record["type"] == "span.begin" && (string)record["track"] == "agents/inspector/tools");
        Assert.Equal((string)inspector["ids"]["agentSession"], (string)tool["ids"]["agentSession"]);
        Assert.Null(tool["ids"]["turn"]);
        Assert.Equal((string)inspector["ids"]["originTurn"], (string)tool["ids"]["originTurn"]);
        Assert.Single(records, record => (string)record["type"] == "span.end" && (string)record["spanId"] == (string)tool["spanId"]);
        var result = Assert.Single(records, record => (string)record["name"] == "city.tool.result");
        Assert.Equal((string)tool["spanId"], (string)result["spanId"]);
        Assert.Equal((string)inspector["ids"]["agentSession"], (string)result["ids"]["agentSession"]);
        var childRecords = records.Where(record => (string)record["ids"]?["agentSession"] == (string)inspector["ids"]["agentSession"]).ToList();
        Assert.Contains(childRecords, record => (string)record["type"] == "span.update");
        Assert.Contains(childRecords, record => (string)record["type"] == "span.end");
        Assert.All(childRecords, record =>
        {
            Assert.Null(record["ids"]["turn"]);
            Assert.Equal((string)roots[0]["ids"]["turn"], (string)record["ids"]["originTurn"]);
            Assert.Equal((string)roots[0]["ids"]["run"], (string)record["ids"]["originRun"]);
        });
    }

    [Fact]
    public async Task Concurrent_session_listeners_do_not_copy_other_sessions_spans()
    {
        async IAsyncEnumerable<ChatResponseUpdate> Respond(List<ChatMessage> input, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, input.Last().Text + " reply");
        }
        using var first = Open(new Client(Respond));
        using var second = Open(new Client(Respond));
        await Task.WhenAll(SessionTurn.RunAsync(first, "first city"), SessionTurn.RunAsync(second, "second city")).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(first.CloseAsync(), second.CloseAsync());
        foreach (var session in new[] { first, second })
        {
            var records = Read(session);
            Assert.Single(records, record => (string)record["type"] == "span.begin" && (string)record["track"] == "agents/mayor/llm");
            Assert.All(records.Where(record => (string)record["track"] == "agents/mayor/llm"), record => Assert.Equal(session.Record.SessionId, (string)record["ids"]["conversation"]));
        }
    }

    private static ClientSession Open(Client client) => new ClientSession(_ => client, new Tools(), "City mayor instructions",
        128000, 16384, true, new SessionRecordOptions { Root = Path.Combine(Path.GetTempPath(), "airimayor-trace-" + Guid.NewGuid().ToString("N")) });

    private static List<JsonNode> Read(ClientSession session)
    {
        using var stream = new FileStream(Path.Combine(session.Record.Directory, SessionRecord.RecordsFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var records = new List<JsonNode>();
        while (reader.ReadLine() is string line) records.Add(JsonNode.Parse(line));
        return records;
    }

    private static JsonNode Resolve(ClientSession session, JsonNode value) => value is JsonObject obj && obj["$asset"] != null
        ? JsonNode.Parse(File.ReadAllText(Path.Combine(session.Record.Directory, (string)obj["$asset"]))) : value;

    private static ChatResponseUpdate Call(string id, string name, Dictionary<string, object> args) => new ChatResponseUpdate(ChatRole.Assistant,
        new List<AIContent> { new FunctionCallContent(id, name, args) });

    private sealed class Client : IChatClient
    {
        private readonly Func<List<ChatMessage>, ChatOptions, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> m_Stream;
        public Client(Func<List<ChatMessage>, ChatOptions, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream) => m_Stream = stream;
        public object GetService(Type type, object key = null) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken: cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default) =>
            m_Stream(messages.ToList(), options, cancellationToken);
    }

    private sealed class Tools : IAgentTools
    {
        public IReadOnlyList<AgentToolDeclaration> List(bool vision) => new[] { new AgentToolDeclaration
        {
            Name = "city_read", Description = "Read city facts", ParametersJson = "{\"type\":\"object\",\"properties\":{}}",
        } };
        public Task<AgentToolResult> InvokeAsync(string name, string arguments, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentToolResult { Success = true, Text = "river facts", Evidence = new JsonObject { ["probe"] = true } });
    }
}
