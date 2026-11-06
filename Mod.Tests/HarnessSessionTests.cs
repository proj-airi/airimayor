using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using airimayor.Agent;
using Microsoft.Extensions.AI;
using Xunit;

public sealed class HarnessSessionTests
{
    private static readonly string Records = Path.Combine(Path.GetTempPath(), "airimayor-harness-" + Guid.NewGuid().ToString("N"));
    private static async Task Wait(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("scenario did not settle");
            await Task.Delay(10);
        }
    }

    private static IReadOnlyList<ChatResponseUpdate> Text(string text) => new[]
    {
        new ChatResponseUpdate(ChatRole.Assistant, text)
    };
    private static IReadOnlyList<ChatResponseUpdate> Calls(params (string id, string name, IDictionary<string, object> args)[] calls) => new[]
    {
        new ChatResponseUpdate(ChatRole.Assistant, calls.Select(call => (AIContent)new FunctionCallContent(call.id, call.name, call.args)).ToList())
    };
    private static Dictionary<string, object> Empty() => new();
    private static ClientSession Open(Client client, Tools tools = null, int window = 128000, int output = 16384)
    {
        return new ClientSession(_ => client, tools ?? new Tools(), "City mayor instructions", window, output, true, new SessionRecordOptions { Root = Records });
    }

    private static Task Prompt(ClientSession session, string text) => FinishTurn(session, () => session.Prompt(text));
    private static Task Stop(ClientSession session) => FinishTurn(session, session.Cancel);

    private static async Task FinishTurn(ClientSession session, Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(SessionUpdate update)
        {
            if (update.Kind == "turn")
                done.TrySetResult();
        }

        session.Updated += Observe;
        try
        {
            action();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            session.Updated -= Observe;
        }
    }

    [Fact]
    public async Task Stop_leaves_pending_steering_for_the_next_prompt_in_arrival_order()
    {
        var entered = new TaskCompletionSource();
        var client = new Client(async (n, _, _, ct) =>
        {
            if (n == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Text("resumed");
        });
        using var session = Open(client);
        session.Prompt("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Prompt("steer");
        await Stop(session);
        await Task.Delay(100);
        Assert.True(client.Calls == 1, "pending steering restarted the stopped run");
        await Prompt(session, "next");
        var sent = new[]
        {
            "first",
            "steer",
            "next"
        };
        Assert.Equal(sent, client.LastInput.Where(item => item.Role == ChatRole.User && sent.Contains(item.Text)).Select(item => item.Text));
        await session.CloseAsync();
    }

    [Fact]
    public async Task Connection_failure_reaches_chat_and_the_record_closes()
    {
        using var session = new ClientSession(_ => throw new InvalidOperationException("connection failed"), new Tools(), "City mayor instructions", 128000, 16384, true, new SessionRecordOptions { Root = Records });
        var turn = await SessionTurn.RunAsync(session, "hello").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(turn.Failed);
        Assert.Equal("connection failed", turn.Error);
        await session.CloseAsync();
        Assert.Equal("source.stopped", (string)JsonNode.Parse(File.ReadLines(Path.Combine(session.Record.Directory, SessionRecord.RecordsFile)).Last())["type"]);
    }

    [Fact]
    public async Task City_tool_failure_keeps_its_error_row_and_arguments()
    {
        var tools = new Tools
        {
            Run = _ => throw new InvalidOperationException("city tool failure")
        };
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(("failed", "city_read", new Dictionary<string, object> { ["district"] = "river" })) : Text("reported")));
        using var session = Open(client, tools);
        await Prompt(session, "inspect");
        var row = JsonNode.Parse(session.ChatStateJson())["messages"].AsArray().Single(item => (string)item["role"] == "tool");
        Assert.Equal("Error", (string)row["status"]);
        Assert.Contains("river", (string)row["arguments"]);
        Assert.Contains("city tool failure", (string)row["text"]);
        await session.CloseAsync();
    }

    [Fact]
    public async Task City_tools_run_sequentially_and_keep_host_evidence_out_of_model_results()
    {
        var tools = new Tools();
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(("one", "city_read", Empty()), ("two", "city_read", Empty())) : Text("done")));
        using var session = Open(client, tools);
        await Prompt(session, "inspect");
        Assert.Equal(2, tools.Count);
        Assert.Equal(1, tools.MaxActive);
        Assert.True(client.Catalog.All(name => name == "city_read" || name.StartsWith("background_agents_") || name.StartsWith("todos_")), "unexpected tools");
        await session.CloseAsync();
        var calls = File.ReadLines(Path.Combine(session.Record.Directory, SessionRecord.RecordsFile))
            .Select(line => JsonNode.Parse(line))
            .Where(line => (string)line["name"] == "city.tool.result")
            .Select(line => JsonNode.Parse(File.ReadAllText(Path.Combine(session.Record.Directory, (string)line["attrs"]["airimayor.data"]["$asset"]))))
            .ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call =>
        {
            Assert.True((bool)call["evidence"]["probe"]);
            Assert.Equal(1L, (long)call["queuedMs"]);
            Assert.Equal(1L, (long)call["timeline"]["applied"]["frame"]);
        });
        Assert.All(calls, call => Assert.NotEmpty(call["files"].AsArray()));
        var results = client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal("{}", result.Result));
        Assert.True(session.ChatStateJson().Contains("city_read"), "city UI tool name lost");
    }

    [Fact]
    public async Task Todo_plan_survives_the_next_player_message()
    {
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(("todo", "todos_add", new Dictionary<string, object> { ["todos"] = new[] { new { title = "keep lights on", description = "city commitment" } } })) : Text("noted")));
        using var session = Open(client);
        await Prompt(session, "plan");
        await Prompt(session, "continue");
        Assert.True(session.ChatStateJson().Contains("keep lights on"), "todo missing");
        var plan = JsonNode.Parse(session.ChatStateJson())["plan"];
        Assert.Equal("pending", (string)Assert.Single(plan["entries"].AsArray())["status"]);
        await session.CloseAsync();
    }

    [Fact]
    public async Task Steering_waits_for_the_active_city_tool()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var tools = new Tools
        {
            Run = async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return "ok";
            }
        };
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(("hold", "city_read", Empty())) : Text("steered")));
        using var session = Open(client, tools);
        session.Prompt("start");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Prompt("steer");
        await Task.Delay(50);
        Assert.True(!tools.Cancelled && client.Calls == 1, "steering cancelled or bypassed tool");
        release.SetResult();
        await Wait(() => client.Calls >= 2 && session.Status == AgentStatus.Idle);
        Assert.True(client.LastInput.Any(item => item.Text == "steer"), "steering missing");
        await session.CloseAsync();
    }

    [Fact]
    public async Task Steering_reaches_the_next_model_call_without_cancelling_generation()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        bool cancelled = false;
        var client = new Client(async (n, _, _, ct) =>
        {
            if (n == 1)
            {
                entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            }

            return Text(n == 1 ? "old reply" : "new reply");
        });
        using var session = Open(client);
        session.Prompt("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Prompt("steer");
        await Task.Delay(50);
        Assert.True(client.Calls == 1 && !cancelled, "native steering interrupted generation");
        release.SetResult();
        await Wait(() => client.Calls == 2 && session.Status == AgentStatus.Idle);
        Assert.True(client.LastInput.Any(item => item.Text == "steer"), "queued steering lost");
        await session.CloseAsync();
    }

    [Fact]
    public async Task Stop_cancels_the_active_tool_and_skips_the_rest_of_its_batch()
    {
        var entered = new TaskCompletionSource();
        var tools = new Tools
        {
            Run = async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return "never";
            }
        };
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(("cancelled", "city_read", Empty()), ("not-started", "city_read", Empty())) : Text("resumed")));
        using var session = Open(client, tools);
        session.Prompt("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Stop(session);
        Assert.True(tools.Cancelled, "tool not cancelled");
        await Prompt(session, "next");
        Assert.True(tools.Count == 1, "remaining batch executed");
        await session.CloseAsync();
    }

    [Fact]
    public async Task Stop_during_generation_keeps_completed_tool_results()
    {
        var entered = new TaskCompletionSource();
        var client = new Client(async (n, _, _, ct) =>
        {
            if (n == 1)
                return Calls(("completed", "city_read", Empty()));
            if (n == 2)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Text("next");
        });
        using var session = Open(client);
        session.Prompt("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Stop(session);
        await Prompt(session, "next");
        var result = client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Single(item => item.CallId == "completed");
        Assert.True(result.Result as string == "{}", "completed result replaced by interruption");
        await session.CloseAsync();
    }

    [Fact]
    public async Task Stop_during_the_first_generation_keeps_the_player_commitment()
    {
        var entered = new TaskCompletionSource();
        var client = new Client(async (n, _, _, ct) =>
        {
            if (n == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Text("next");
        });
        using var session = Open(client);
        session.Prompt("original commitment");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Stop(session);
        await Prompt(session, "next");
        Assert.Equal(2, client.Calls);
        Assert.True(client.LastInput.Any(item => item.Text == "original commitment"), "first prompt lost: " +
            string.Join("\n", client.LastInput.Select(item => item.Role + ":" + item.Text)));
        await session.CloseAsync();
    }

    [Fact]
    public async Task City_images_remain_in_tool_results_across_turns()
    {
        var tools = new Tools
        {
            Image = new byte[]
            {
                137,
                80,
                78,
                71
            }
        };
        var client = new Client((n, _, _, _) => Task.FromResult(n == 1 ? Calls(("image-1", "city_read", Empty()), ("image-2", "city_read", Empty())) : Text("seen")));
        using var session = Open(client, tools);
        await Prompt(session, "look");
        await Prompt(session, "again");
        var results = client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>().ToList();
        Assert.True(results.Count == 2 && results.All(item => ((IEnumerable<AIContent>)item.Result).OfType<DataContent>().Count() == 1), "native result image lost");
        Assert.True(client.LastInput.Where(item => item.Role == ChatRole.User).All(item => !item.Contents.OfType<DataContent>().Any()), "synthetic image messages remain");
        await session.CloseAsync();
    }

    [Fact]
    public async Task Long_tool_runs_compact_model_input_and_keep_the_latest_result()
    {
        var tools = new Tools
        {
            Run = _ => Task.FromResult(new string ('x', 18000))
        };
        var client = new Client((n, _, _, _) => Task.FromResult(n <= 10 ? Calls(("read-" + n, "city_read", Empty())) : Text("done")));
        using var session = Open(client, tools, 16000, 1000);
        await Prompt(session, "long task");
        Assert.Equal(10, tools.Count);
        var results = client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>().ToList();
        Assert.True(results.Sum(item => JsonSerializer.Serialize(item.Result).Length) < 10 * 18000, "model still received all ten full tool results");
        Assert.Contains(results, result => result.CallId == "read-10");
        await session.CloseAsync();
    }

    [Fact]
    public async Task Background_completion_stays_idle_until_the_player_requests_results()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        int parent = 0, child = 0;
        var client = new Client(async (_, _, options, ct) =>
        {
            if (!options.Tools.Any(tool => tool.Name == "background_agents_start_task"))
            {
                Interlocked.Increment(ref child);
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Text("inspection report");
            }

            int step = Interlocked.Increment(ref parent);
            return step == 1 ? Calls(("spawn", "background_agents_start_task", new Dictionary<string, object> { ["agentName"] = "inspector", ["input"] = "inspect city", ["description"] = "inspection" })) : step == 3 ? Calls(("wait", "background_agents_wait_for_first_completion", new Dictionary<string, object> { ["taskIds"] = new[] { 1 } })) : step == 4 ? Calls(("results", "background_agents_get_task_results", new Dictionary<string, object> { ["taskId"] = 1 })) : Text("parent done");
        });
        using var session = Open(client);
        session.Prompt("delegate");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Wait(() => parent == 2 && session.Status == AgentStatus.Idle);
        session.Cancel();
        release.SetResult();
        await Wait(() => child == 1);
        await Task.Delay(150);
        Assert.True(parent == 2, "native child auto-woke root");
        await Prompt(session, "get report");
        Assert.Equal(5, parent);
        Assert.Contains(client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>(), result => JsonSerializer.Serialize(result.Result).Contains("inspection report"));
        await session.CloseAsync();
    }

    [Fact]
    public async Task Closing_the_session_cancels_background_work()
    {
        var entered = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        int parent = 0;
        var client = new Client(async (_, _, options, ct) =>
        {
            if (!options.Tools.Any(tool => tool.Name == "background_agents_start_task"))
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }
            }

            return Interlocked.Increment(ref parent) == 1 ? Calls(("spawn", "background_agents_start_task", new Dictionary<string, object> { ["agentName"] = "inspector", ["input"] = "inspect", ["description"] = "inspection" })) : Text("done");
        });
        using var session = Open(client);
        session.Prompt("delegate");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Task.IsCompleted, "native child was not released");
    }

    [Fact]
    public async Task Chat_keeps_pointed_places_separate_from_model_text_and_bounds_the_transcript()
    {
        var client = new Client((_, _, _, _) => Task.FromResult(Text("ok")));
        using var session = Open(client);
        session.Prompt("model city coordinates", "here", "[{\"label\":\"square\"}]");
        await Wait(() => client.Calls == 1 && session.Status == AgentStatus.Idle);
        Assert.True(session.ChatStateJson().Contains("square") && client.LastInput.Any(item => item.Text == "model city coordinates"), "places projection lost");
        Assert.True(!client.LastInput.Any(item => (item.Text ?? "").Contains("square")), "places leaked into model text");
        for (int i = 0; i < 110; i++)
            await Prompt(session, "next " + i);
        var state = JsonNode.Parse(session.ChatStateJson());
        Assert.True(state["messages"].AsArray().Count <= 200, "UI history unbounded");
        Assert.True(!string.IsNullOrEmpty((string)state["session"]), "UI session missing");
        await session.CloseAsync();
    }

    [Fact]
    public async Task Stop_keeps_the_background_task_id_and_leaves_the_child_running()
    {
        var entered = new TaskCompletionSource();
        var childEntered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        bool childCancelled = false;
        int parent = 0;
        var client = new Client(async (_, _, options, ct) =>
        {
            if (!options.Tools.Any(tool => tool.Name == "background_agents_start_task"))
            {
                childEntered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    childCancelled = true;
                    throw;
                }

                return Text("child done");
            }

            int step = Interlocked.Increment(ref parent);
            if (step == 1)
                return Calls(("spawn", "background_agents_start_task", new Dictionary<string, object> { ["agentName"] = "inspector", ["input"] = "inspect city", ["description"] = "inspection" }));
            if (step == 2)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Text("continued");
        });
        using var session = Open(client);
        session.Prompt("delegate");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Stop(session);
        Assert.True(!childCancelled, "root Stop cancelled native child");
        await Prompt(session, "continue");
        var spawn = client.LastInput.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Single(item => item.CallId == "spawn");
        Assert.True(!JsonSerializer.Serialize(spawn.Result).Contains("interrupted"), "native tool result replaced by interruption");
        release.SetResult();
        await session.CloseAsync();
    }

    [Fact]
    public async Task Failed_child_continuation_keeps_completed_city_results()
    {
        int parent = 0, child = 0;
        List<ChatMessage> resumed = null;
        var failed = new TaskCompletionSource();
        var client = new Client((_, input, options, _) =>
        {
            if (!options.Tools.Any(tool => tool.Name == "background_agents_start_task"))
            {
                int step = Interlocked.Increment(ref child);
                if (step == 1)
                    return Task.FromResult(Calls(("child-city", "city_read", Empty())));
                if (step == 2)
                {
                    failed.SetResult();
                    throw new InvalidOperationException("child model failure");
                }

                resumed = input;
                return Task.FromResult(Text("child recovered"));
            }

            int root = Interlocked.Increment(ref parent);
            return Task.FromResult(root == 1 ? Calls(("spawn", "background_agents_start_task", new Dictionary<string, object> { ["agentName"] = "inspector", ["input"] = "inspect", ["description"] = "inspection" })) : root == 3 ? Calls(("wait-failure", "background_agents_wait_for_first_completion", new Dictionary<string, object> { ["taskIds"] = new[] { 1 } })) : root == 4 ? Calls(("resume", "background_agents_continue_task", new Dictionary<string, object> { ["taskId"] = 1, ["text"] = "continue" })) : root == 5 ? Calls(("wait-success", "background_agents_wait_for_first_completion", new Dictionary<string, object> { ["taskIds"] = new[] { 1 } })) : Text("done"));
        });
        using var session = Open(client);
        session.Prompt("delegate");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Wait(() => parent == 2 && session.Status == AgentStatus.Idle);
        await Prompt(session, "resume child");
        Assert.True(child == 3 && resumed != null, "native child did not continue");
        var result = resumed.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Single(item => item.CallId == "child-city");
        Assert.True(result.Result as string == "{}", "child completed city result lost");
        await session.CloseAsync();
    }

    [Fact]
    public async Task Provider_context_errors_reach_chat_without_a_custom_retry()
    {
        var client = new Client((_, _, _, _) => throw new InvalidOperationException("context_length_exceeded"));
        using var session = Open(client);
        await Prompt(session, "oversized");
        Assert.True(client.Calls == 1 && session.ChatStateJson().Contains("context_length_exceeded"), "bespoke retry still active");
        await session.CloseAsync();
    }

    private sealed class Client : IChatClient
    {
        private readonly Func<int, List<ChatMessage>, ChatOptions, CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>> Handler;
        public int Calls;
        public List<ChatMessage> LastInput = new();
        public List<string> Catalog = new();
        public Client(Func<int, List<ChatMessage>, ChatOptions, CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>> handler) => Handler = handler;
        public object GetService(Type type, object key = null) => null;
        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default) => GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken: cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var input = messages.ToList();
            var pending = new HashSet<string>();
            foreach (var message in input)
            {
                if (message.Role == ChatRole.User)
                    Assert.True(pending.Count == 0, "user image/message split tool result batch");
                foreach (var content in message.Contents)
                    if (content is FunctionCallContent call)
                        pending.Add(call.CallId);
                    else if (content is FunctionResultContent result)
                        Assert.True(pending.Remove(result.CallId), "orphan/duplicate result " + result.CallId);
            }

            Assert.True(pending.Count == 0, "unpaired function calls: " + string.Join(",", pending));
            Assert.True((options.Instructions ?? "").Contains("City mayor instructions") || input.Any(message => message.Role == ChatRole.System && (message.Text ?? "").Contains("City mayor instructions")), "system prompt missing");
            LastInput = input;
            Catalog = options.Tools.Select(tool => tool.Name).ToList();
            var response = await Handler(Interlocked.Increment(ref Calls), input, options, cancellationToken);
            foreach (var update in response)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }
    }

    private sealed class Tools : IAgentTools
    {
        public Func<CancellationToken, Task<string>> Run = _ => Task.FromResult("{}");
        public int Count, Active, MaxActive;
        public bool Cancelled;
        public byte[] Image;
        public IReadOnlyList<AgentToolDeclaration> List(bool vision) => new[]
        {
            new AgentToolDeclaration
            {
                Name = "city_read",
                Description = "Read city facts",
                ParametersJson = "{\"type\":\"object\",\"properties\":{}}"
            }
        };
        public async Task<AgentToolResult> InvokeAsync(string name, string arguments, CancellationToken ct)
        {
            Count++;
            int active = Interlocked.Increment(ref Active);
            MaxActive = Math.Max(MaxActive, active);
            try
            {
                return new AgentToolResult
                {
                    Success = true,
                    Text = await Run(ct),
                    ImagePng = Image,
                    Applied = new SimulationStamp(1, "probe", 0),
                    QueuedMs = 1,
                    Evidence = new JsonObject
                    {
                        ["probe"] = true
                    },
                    Files = new Dictionary<string, byte[]>
                    {
                        ["probe.txt"] = System.Text.Encoding.UTF8.GetBytes("host evidence file")
                    }
                };
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref Active);
            }
        }
    }
}
