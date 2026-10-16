using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRuntime;
using Microsoft.Extensions.AI;
using Xunit;

namespace airimayor.Agent
{
    public sealed class AgentRuntimeTests
    {
        [Fact]
        public async Task Prompt_records_the_player_line()
        {
            using (AgentRuntime.AgentRuntime runtime = Open(
                new QuietTools(),
                new ScriptedChatClient(Text("Noted."))))
            {
                runtime.Prompt("hello");
                await WaitUntilAsync(() => runtime.Status == AgentStatus.Idle && runtime.ChatStateJson().Contains("hello"));
                using (JsonDocument state = JsonDocument.Parse(runtime.ChatStateJson()))
                {
                    Assert.Contains("hello", state.RootElement.GetProperty("messages").ToString());
                }
            }
        }

        [Fact]
        public async Task Player_message_waits_for_the_in_flight_call_and_keeps_the_plan()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tools = new HoldingTools(started);
            var client = new ScriptedChatClient(
                ToolCall("plan-1", "set_plan", PlanEntries()),
                ToolCall("hold-1", "hold", new Dictionary<string, object>()),
                Text("Done."));
            using (AgentRuntime.AgentRuntime runtime = Open(tools, client))
            {
                runtime.Prompt("start");
                Assert.True(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                runtime.Prompt("steer");
                await Task.Delay(100);
                Assert.False(tools.Cancelled);
                Assert.Equal(AgentStatus.Working, runtime.Status);
                tools.Release();
                await WaitUntilAsync(() => runtime.Status == AgentStatus.Idle && runtime.ChatStateJson().Contains("steer"));
                using (JsonDocument state = JsonDocument.Parse(runtime.ChatStateJson()))
                {
                    JsonElement plan = state.RootElement.GetProperty("plan");
                    JsonElement entries = plan.GetProperty("entries");
                    Assert.Equal(2, entries.GetArrayLength());
                    Assert.Equal("keep the lights on", entries[0].GetProperty("content").GetString());
                    Assert.Equal("in_progress", entries[0].GetProperty("status").GetString());
                    Assert.Equal("power stable", entries[1].GetProperty("content").GetString());
                }
                Assert.False(tools.Cancelled);
            }
        }

        [Fact]
        public async Task Cancel_stops_the_in_flight_call_and_keeps_the_plan()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tools = new HoldingTools(started);
            var client = new ScriptedChatClient(
                ToolCall("plan-1", "set_plan", PlanEntries()),
                ToolCall("hold-1", "hold", new Dictionary<string, object>()));
            using (AgentRuntime.AgentRuntime runtime = Open(tools, client))
            {
                runtime.Prompt("start");
                Assert.True(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                runtime.Cancel();
                await WaitUntilAsync(() => runtime.Status == AgentStatus.Idle && !runtime.IsBusy);
                using (JsonDocument state = JsonDocument.Parse(runtime.ChatStateJson()))
                {
                    JsonElement plan = state.RootElement.GetProperty("plan");
                    Assert.Equal("keep the lights on", plan.GetProperty("entries")[0].GetProperty("content").GetString());
                }
                Assert.True(tools.Cancelled);
            }
        }

        private static Dictionary<string, object> PlanEntries()
        {
            return new Dictionary<string, object>
            {
                ["entries"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["content"] = "keep the lights on",
                        ["priority"] = "high",
                        ["status"] = "in_progress",
                    },
                    new Dictionary<string, object>
                    {
                        ["content"] = "power stable",
                        ["priority"] = "medium",
                        ["status"] = "pending",
                    },
                },
            };
        }

        [Fact]
        public async Task Cancel_leaves_a_running_conversation_and_dispose_stops_it()
        {
            var client = new HoldingChildClient();
            AgentRuntime.AgentRuntime runtime = Open(new QuietTools(), client);
            runtime.Prompt("start");
            Assert.True(await client.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            runtime.Cancel();
            await Task.Delay(150);
            Assert.Equal(0, client.ChildCancelled);
            runtime.Dispose();
            await WaitUntilAsync(() => client.ChildCancelled == 1);
        }

        [Fact]
        public async Task Each_model_round_is_recorded_with_its_request_and_reply()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "airimayor-record-" + Guid.NewGuid().ToString("N"));
            string prompt = new string('p', SessionRecord.BlobChars);
            var client = new ScriptedChatClient(ToolCall("plan-1", "set_plan", PlanEntries()), Text("Done."));
            try
            {
                string directory;
                using (var runtime = new AgentRuntime.AgentRuntime(
                    new QuietTools(),
                    prompt,
                    () => new ModelSettings { Model = "test", WindowTokens = 32_000, Wire = ModelWire.ChatCompletions },
                    new SessionRecordOptions { Root = root },
                    client))
                {
                    directory = runtime.Record.Directory;
                    runtime.Prompt("start");
                    await WaitUntilAsync(() => runtime.Status == AgentStatus.Idle && runtime.ChatStateJson().Contains("Done."));
                }

                string[] lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(directory, SessionRecord.ModelCallsFile));
                Assert.Equal(2, lines.Length);
                using (JsonDocument first = JsonDocument.Parse(lines[0]))
                using (JsonDocument second = JsonDocument.Parse(lines[1]))
                using (JsonDocument session = JsonDocument.Parse(System.IO.File.ReadAllText(
                    System.IO.Path.Combine(directory, SessionRecord.SessionFile))))
                {
                    JsonElement call = first.RootElement.GetProperty("data");
                    Assert.Equal("turn", call.GetProperty("purpose").GetString());
                    JsonElement system = call.GetProperty("request").GetProperty("messages")[0];
                    Assert.Equal("system", system.GetProperty("role").GetString());
                    string blob = system.GetProperty("content")[0].GetProperty("blob").GetString();
                    Assert.Equal(prompt, System.IO.File.ReadAllText(System.IO.Path.Combine(directory, blob)));
                    Assert.Equal(blob, session.RootElement.GetProperty("prompt").GetProperty("blob").GetString());
                    string catalog = call.GetProperty("request").GetProperty("catalog").GetString();
                    Assert.True(System.IO.File.Exists(System.IO.Path.Combine(directory, "catalogs", catalog + ".json")));
                    Assert.Equal("plan-1", call.GetProperty("outcome").GetProperty("toolCalls")[0].GetProperty("id").GetString());
                    Assert.Equal("Done.", second.RootElement.GetProperty("data").GetProperty("outcome").GetProperty("text").GetString());
                }
            }
            finally
            {
                if (System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.Delete(root, true);
                }
            }
        }

        [Fact]
        public async Task A_conversation_does_not_continue_or_receive_task()
        {
            var client = new ChildOnceClient();
            using (AgentRuntime.AgentRuntime runtime = Open(new QuietTools(), client, continueWhenIdle: true))
            {
                runtime.Prompt("start");
                await WaitUntilAsync(() => client.ChildStarts == 1 && runtime.ChatStateJson().Contains("studied"));
                await Task.Delay(150);
                Assert.Equal(1, client.ChildStarts);
                Assert.Equal(0, client.ChildFollows);
                Assert.False(client.ChildSawTask);
            }
        }

        private static AgentRuntime.AgentRuntime Open(IAgentTools tools, IChatClient client, bool continueWhenIdle = false)
        {
            return new AgentRuntime.AgentRuntime(
                tools,
                "instructions",
                () => new ModelSettings
                {
                    Model = "test",
                    WindowTokens = 32_000,
                    Wire = ModelWire.ChatCompletions,
                    ContinueWhenIdle = continueWhenIdle,
                },
                null,
                client);
        }

        private static async Task WaitUntilAsync(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ready())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("runtime did not settle");
                }
                await Task.Delay(20);
            }
        }

        private static IReadOnlyList<ChatResponseUpdate> Text(string text)
        {
            return new[]
            {
                new ChatResponseUpdate(ChatRole.Assistant, text),
            };
        }

        private static IReadOnlyList<ChatResponseUpdate> ToolCall(
            string callId,
            string name,
            IDictionary<string, object> arguments)
        {
            return new[]
            {
                new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent(callId, name, arguments),
                }),
            };
        }

        private sealed class QuietTools : IAgentTools
        {
            public IReadOnlyList<AgentToolDeclaration> List(bool visionAvailable)
            {
                return Array.Empty<AgentToolDeclaration>();
            }

            public Task<AgentToolResult> InvokeAsync(
                string name,
                string argumentsJson,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new AgentToolResult { Success = true, Text = "{}" });
            }
        }

        private sealed class HoldingTools : IAgentTools
        {
            private readonly TaskCompletionSource<bool> m_Started;
            private readonly TaskCompletionSource<bool> m_Release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public HoldingTools(TaskCompletionSource<bool> started)
            {
                m_Started = started;
            }

            public bool Cancelled { get; private set; }

            public void Release()
            {
                m_Release.TrySetResult(true);
            }

            public IReadOnlyList<AgentToolDeclaration> List(bool visionAvailable)
            {
                return new[]
                {
                    new AgentToolDeclaration
                    {
                        Name = "hold",
                        Description = "block until released",
                        ParametersJson = "{\"type\":\"object\",\"properties\":{}}",
                    },
                };
            }

            public async Task<AgentToolResult> InvokeAsync(
                string name,
                string argumentsJson,
                CancellationToken cancellationToken)
            {
                m_Started.TrySetResult(true);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    Task finished = await Task.WhenAny(m_Release.Task, cancelled.Task);
                    if (finished == cancelled.Task)
                    {
                        Cancelled = true;
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
                return new AgentToolResult { Success = true, Text = "{}" };
            }
        }

        private sealed class ScriptedChatClient : IChatClient
        {
            private readonly Queue<IReadOnlyList<ChatResponseUpdate>> m_Scripts;

            public ScriptedChatClient(params IReadOnlyList<ChatResponseUpdate>[] scripts)
            {
                m_Scripts = new Queue<IReadOnlyList<ChatResponseUpdate>>(scripts);
            }

            public ChatClientMetadata Metadata => new ChatClientMetadata("script");

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                if (m_Scripts.Count == 0)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "idle");
                    yield break;
                }
                foreach (ChatResponseUpdate update in m_Scripts.Dequeue())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return update;
                    await Task.Yield();
                }
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class HoldingChildClient : IChatClient
        {
            private int m_ParentCalls;

            public TaskCompletionSource<bool> ChildEntered { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public int ChildCancelled;

            public ChatClientMetadata Metadata => new ChatClientMetadata("hold-child");

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                if (IsChild(messages))
                {
                    ChildEntered.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref ChildCancelled);
                        throw;
                    }
                    yield break;
                }
                if (Interlocked.Increment(ref m_ParentCalls) == 1)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                    {
                        new FunctionCallContent("task-1", "task", new Dictionary<string, object>
                        {
                            ["description"] = "Wells",
                            ["prompt"] = "inspect the wells",
                        }),
                    });
                    yield break;
                }
                yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class ChildOnceClient : IChatClient
        {
            private int m_ParentCalls;

            public int ChildStarts;
            public int ChildFollows;
            public bool ChildSawTask;

            public ChatClientMetadata Metadata => new ChatClientMetadata("child-once");

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                bool child = IsChild(messages);
                bool follow = child && HasText(messages, "studied");
                if (follow)
                {
                    Interlocked.Increment(ref ChildFollows);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "again");
                    yield break;
                }
                if (child)
                {
                    Interlocked.Increment(ref ChildStarts);
                    if (options?.Tools != null)
                    {
                        foreach (AITool tool in options.Tools)
                        {
                            if (tool.Name != "task")
                            {
                                continue;
                            }
                            ChildSawTask = true;
                            break;
                        }
                    }
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "studied");
                    yield break;
                }
                if (Interlocked.Increment(ref m_ParentCalls) == 1)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                    {
                        new FunctionCallContent("task-1", "task", new Dictionary<string, object>
                        {
                            ["description"] = "Wells",
                            ["prompt"] = "inspect the wells",
                        }),
                    });
                    yield break;
                }
                yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
                await Task.Yield();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        private static bool IsChild(IEnumerable<ChatMessage> messages)
        {
            return HasText(messages, "inspect the wells");
        }

        private static bool HasText(IEnumerable<ChatMessage> messages, string text)
        {
            foreach (ChatMessage message in messages)
            {
                if ((message.Text ?? "").Contains(text))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
