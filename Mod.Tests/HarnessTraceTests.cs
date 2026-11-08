using System.Diagnostics;
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
        async IAsyncEnumerable<ChatResponseUpdate> Respond(List<ChatMessage> input, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var update = Read(session).Single(record => (string)record["type"] == "span.update" && (string)record["track"] == "agents/mayor/llm");
            Assert.Contains("inspect the river", Resolve(session, update["attrs"]["gen_ai.input.messages"]).ToJsonString());
            Assert.Contains("city_read", Resolve(session, update["attrs"]["gen_ai.tool.definitions"]).ToJsonString());
            Assert.Contains("City mayor instructions", Resolve(session, update["attrs"]["gen_ai.system_instructions"]).ToJsonString());
            Assert.Equal(input.Where(message => message.MessageId != null).Select(message => message.MessageId),
                update["attrs"]["airimayor.input.message_ids"].AsArray().Select(id => (string)id));
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
        var delta = Assert.Single(Read(session), record => (string)record["name"] == "model.output.delta");
        Assert.Equal("kept partial reply", (string)Resolve(session, delta["attrs"]["airimayor.data"])["text"]);
    }

    [Fact]
    public async Task Stream_fragments_are_durable_while_the_model_waits_and_keep_typed_content()
    {
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string inputId = null;
        async IAsyncEnumerable<ChatResponseUpdate> Respond(List<ChatMessage> input, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            inputId = input.Single(message => message.Role == ChatRole.User && message.MessageId != null).MessageId;
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new TextContent("first fragment"), new TextReasoningContent("first returned reasoning"),
            })
            {
                MessageId = "streamed-message", ResponseId = "streamed-response", ModelId = "streamed-model",
                RawRepresentation = new { authorization = "raw-secret-must-not-serialize" },
                AdditionalProperties = new AdditionalPropertiesDictionary { ["apiKey"] = "credential-must-be-redacted" },
            };
            firstReady.TrySetResult();
            await allowSecond.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new TextContent("second fragment"), new TextReasoningContent("second returned reasoning"),
                new FunctionCallContent("streamed-tool", "city_read", new Dictionary<string, object> { ["district"] = "river" }),
            }) { MessageId = "streamed-message", ResponseId = "streamed-response" };
            secondReady.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        using var session = Open(new Client(Respond), "replay");
        session.Prompt("inspect streamed city progress");
        await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstRecords = Read(session);
        var first = Assert.Single(firstRecords, record => (string)record["name"] == "model.output.delta");
        var firstData = Resolve(session, first["attrs"]["airimayor.data"]);
        Assert.Equal(0, (int)firstData["index"]);
        Assert.Equal("first fragment", (string)firstData["text"]);
        Assert.Equal("first returned reasoning", (string)firstData["reasoning"]);
        Assert.Contains("streamed-message", firstData["update"].ToJsonString());
        Assert.DoesNotContain("raw-secret-must-not-serialize", firstData.ToJsonString());
        Assert.DoesNotContain("credential-must-be-redacted", firstData.ToJsonString());
        Assert.DoesNotContain(firstRecords, record => (string)record["type"] == "span.end" && (string)record["spanId"] == (string)first["spanId"]);
        var applied = Assert.Single(firstRecords, record => (string)record["name"] == "input.applied");
        Assert.Equal(inputId, (string)Resolve(session, applied["attrs"]["airimayor.data"])["messageId"]);
        allowSecond.TrySetResult();
        await secondReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRecords = Read(session);
        var fragments = secondRecords.Where(record => (string)record["name"] == "model.output.delta").ToList();
        Assert.Equal(2, fragments.Count);
        var secondData = Resolve(session, fragments[1]["attrs"]["airimayor.data"]);
        Assert.Equal(1, (int)secondData["index"]);
        Assert.Equal("second fragment", (string)secondData["text"]);
        Assert.Equal("second returned reasoning", (string)secondData["reasoning"]);
        Assert.Contains("streamed-tool", secondData["update"].ToJsonString());
        Assert.Contains("city_read", secondData["update"].ToJsonString());
        Assert.Contains("river", secondData["update"].ToJsonString());
        Assert.All(fragments, fragment => Assert.Equal((string)first["spanId"], (string)fragment["spanId"]));
        Assert.DoesNotContain(secondRecords, record => (string)record["type"] == "span.end" && (string)record["spanId"] == (string)first["spanId"]);
        session.Cancel();
        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var end = Assert.Single(Read(session), record => (string)record["type"] == "span.end" && (string)record["spanId"] == (string)first["spanId"]);
        Assert.Equal("cancelled", (string)end["status"]);
        var parts = Resolve(session, end["attrs"]["gen_ai.output.messages"]).AsArray().SelectMany(message => message["parts"].AsArray()).ToList();
        Assert.Equal("first fragmentsecond fragment", string.Concat(parts.Where(part => (string)part["type"] == "text").Select(part => (string)part["content"])));
        Assert.Equal("first returned reasoningsecond returned reasoning", string.Concat(parts.Where(part => (string)part["type"] == "reasoning").Select(part => (string)part["content"])));
    }

    [Fact]
    public async Task Native_events_and_links_keep_original_time_context_and_attributes()
    {
        var external = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, "vendor=sample", true);
        DateTimeOffset eventTime = default;
        async IAsyncEnumerable<ChatResponseUpdate> Respond(List<ChatMessage> input, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Activity activity = Activity.Current;
            eventTime = new DateTimeOffset(activity.StartTimeUtc.AddMilliseconds(2));
            activity.AddEvent(new ActivityEvent("native.phase", eventTime, new ActivityTagsCollection
            {
                ["phase.name"] = "received", ["phase.count"] = 2,
                ["phase.detail"] = new Dictionary<string, object> { ["district"] = "river" },
            }));
            activity.AddLink(new ActivityLink(external, new ActivityTagsCollection { ["link.detail"] = "upstream native work" }));
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "native facts recorded");
        }

        using var session = Open(new Client(Respond), "native-events");
        await SessionTurn.RunAsync(session, "inspect native facts").WaitAsync(TimeSpan.FromSeconds(10));
        await session.CloseAsync();
        var records = Read(session);
        var phase = Assert.Single(records, record => (string)record["name"] == "native.phase");
        var begin = Assert.Single(records, record => (string)record["type"] == "span.begin" && (string)record["spanId"] == (string)phase["spanId"]);
        Assert.Equal(2, (double)phase["ts"] - (double)begin["ts"], 6);
        Assert.Equal(2, (double)phase["attrs"]["otel.event.offset_ms"]);
        Assert.Equal(eventTime.ToString("o"), (string)phase["attrs"]["otel.event.timestamp"]);
        Assert.Equal("received", (string)phase["attrs"]["phase.name"]);
        Assert.Equal(2, (int)phase["attrs"]["phase.count"]);
        Assert.Equal("river", (string)Resolve(session, phase["attrs"]["phase.detail"])["district"]);
        var end = Assert.Single(records, record => (string)record["type"] == "span.end" && (string)record["spanId"] == (string)phase["spanId"]);
        var link = Assert.Single(end["links"].AsArray());
        Assert.Equal("linked", (string)link["rel"]);
        Assert.Equal("otel.span", (string)link["to"]["key"]);
        Assert.Equal(external.TraceId.ToHexString() + "/" + external.SpanId.ToHexString(), (string)link["to"]["id"]);
        var nativeLink = Assert.Single(Resolve(session, end["attrs"]["otel.links"]).AsArray());
        Assert.Equal(external.TraceId.ToHexString(), (string)nativeLink["traceId"]);
        Assert.Equal(external.SpanId.ToHexString(), (string)nativeLink["spanId"]);
        Assert.Equal("vendor=sample", (string)nativeLink["traceState"]);
        Assert.True((bool)nativeLink["remote"]);
        Assert.Equal("upstream native work", (string)nativeLink["attrs"]["link.detail"]);
    }

    [Fact]
    public async Task Native_todo_changes_are_recorded_before_the_next_model_call_without_duplicate_tools()
    {
        ClientSession session = null;
        int step = 0;
        async IAsyncEnumerable<ChatResponseUpdate> Respond(List<ChatMessage> input, ChatOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            switch (++step)
            {
                case 1:
                    yield return Call("add", "todos_add", new Dictionary<string, object>
                    {
                        ["todos"] = new[] { new { title = "Keep the river clean", description = "Inspect the water system" } },
                    });
                    break;
                case 2:
                    Assert.Single(Read(session), record => (string)record["name"] == "plan.changed");
                    yield return Call("list", "todos_get_all", new Dictionary<string, object>());
                    break;
                case 3:
                    yield return Call("complete", "todos_complete", new Dictionary<string, object>
                    {
                        ["items"] = new[] { new { id = 1, reason = "Water inspected" } },
                    });
                    break;
                case 4:
                    yield return Call("remove", "todos_remove", new Dictionary<string, object> { ["ids"] = new[] { 1 } });
                    break;
                default:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "Plan finished");
                    break;
            }
        }

        using (session = Open(new Client(Respond), "plan"))
        {
            var turn = await SessionTurn.RunAsync(session, "Plan the river inspection").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(turn.Failed, turn.Error);
            await session.CloseAsync();
            var records = Read(session);
            var plans = records.Where(record => (string)record["name"] == "plan.changed").Select(record => Resolve(session, record["attrs"]["airimayor.data"])).ToList();
            Assert.Equal(3, plans.Count);
            Assert.Equal("Keep the river clean", (string)plans[0]["entries"][0]["title"]);
            Assert.Equal("Inspect the water system", (string)plans[0]["entries"][0]["description"]);
            Assert.Equal("pending", (string)plans[0]["entries"][0]["status"]);
            Assert.Equal("completed", (string)plans[1]["entries"][0]["status"]);
            Assert.Empty(plans[2]["entries"].AsArray());
            var tools = records.Where(record => (string)record["type"] == "span.begin" && (string)record["track"] == "agents/mayor/tools").ToList();
            Assert.Equal(4, tools.Count);
            Assert.All(tools, tool => Assert.Single(records, record => (string)record["type"] == "span.end" && (string)record["spanId"] == (string)tool["spanId"]));
        }
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

    private static ClientSession Open(Client client, string fixture = "") => new ClientSession(_ => client, new Tools(), "City mayor instructions",
        128000, 16384, true, new SessionRecordOptions { Root = Path.Combine(Path.GetTempPath(), "airimayor-trace-" + fixture + "-" + Guid.NewGuid().ToString("N")) });

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
