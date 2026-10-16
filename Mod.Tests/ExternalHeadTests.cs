using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRuntime;
using Xunit;

namespace airimayor.Agent
{
    public sealed class ExternalHeadTests
    {
        [Fact]
        public void Plan_json_uses_agent_plan_words()
        {
            string json = AcpChatMap.PlanJson(new[]
            {
                new PlanStep { Content = "Lay a collector", Priority = "high", Status = "in_progress" },
            });
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement entry = document.RootElement.GetProperty("entries")[0];
                Assert.Equal("Lay a collector", entry.GetProperty("content").GetString());
                Assert.Equal("high", entry.GetProperty("priority").GetString());
                Assert.Equal("in_progress", entry.GetProperty("status").GetString());
            }
        }

        [Fact]
        public void Tool_updates_merge_by_call_id_and_keep_omitted_fields()
        {
            AcpToolState state = AcpToolState.Apply(null, "call-1", new AcpToolPatch
            {
                HasTitle = true,
                Title = "city_get_demand",
                HasRawInput = true,
                RawInput = "{}",
                HasStatus = true,
                Finished = false,
            });
            state = AcpToolState.Apply(state, "call-1", new AcpToolPatch
            {
                HasRawInput = true,
                RawInput = "{\"limit\":16}",
                HasStatus = true,
                Finished = false,
            });
            state = AcpToolState.Apply(state, "call-1", new AcpToolPatch
            {
                HasContent = true,
                Content = "{\"residential\":1}",
                HasRawOutput = true,
                RawOutput = "{\"output\":\"{\\\"residential\\\":1}\"}",
                HasStatus = true,
                Finished = true,
            });

            SessionUpdate row = AcpChatMap.ToolRow(state);
            Assert.Equal("city_get_demand", row.Tool);
            Assert.Equal("{\"limit\":16}", row.Text);
            Assert.Equal("{\"residential\":1}", row.Result);
            Assert.Equal("{\"output\":\"{\\\"residential\\\":1}\"}", row.Output);
            Assert.Equal(AgentStatus.Idle, row.Status);

            using (JsonDocument document = JsonDocument.Parse(row.ToJsonString()))
            {
                JsonElement root = document.RootElement;
                Assert.Equal("call-1", root.GetProperty("callId").GetString());
                Assert.Equal("city_get_demand", root.GetProperty("tool").GetString());
                Assert.Equal("{\"limit\":16}", root.GetProperty("text").GetString());
                Assert.Equal("{\"residential\":1}", root.GetProperty("result").GetString());
                Assert.Equal("{\"output\":\"{\\\"residential\\\":1}\"}", root.GetProperty("output").GetString());
            }
        }

        [Fact]
        public async Task Bridge_lists_and_invokes_with_the_session_token()
        {
            var tools = new EchoTools();
            var book = new ConversationBook(
                (id, message, cancellationToken) => Task.FromResult("done"),
                _ => { },
                _ => { });
            using (ToolBridge bridge = ToolBridge.Start(tools, () => false, "Be the mayor.", book))
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bridge.Token);
                using (HttpResponseMessage catalog = await http.GetAsync(bridge.BaseUrl + "tools"))
                {
                    catalog.EnsureSuccessStatusCode();
                    string body = await catalog.Content.ReadAsStringAsync();
                    Assert.Contains("echo", body);
                    Assert.Contains("Be the mayor.", body);
                    Assert.Contains("Do not look it up.", body);
                }
                using (var content = new StringContent(
                    "{\"name\":\"echo\",\"argumentsJson\":\"{\\\"message\\\":\\\"hi\\\"}\"}",
                    Encoding.UTF8,
                    "application/json"))
                using (HttpResponseMessage invoked = await http.PostAsync(bridge.BaseUrl + "invoke", content))
                {
                    invoked.EnsureSuccessStatusCode();
                    string body = await invoked.Content.ReadAsStringAsync();
                    Assert.Contains("hi", body);
                    Assert.Contains("aGk=", body);
                }
                using (var childHttp = new HttpClient())
                {
                    childHttp.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", bridge.ChildToken);
                    using (HttpResponseMessage childCatalog = await childHttp.GetAsync(bridge.BaseUrl + "tools"))
                    {
                        childCatalog.EnsureSuccessStatusCode();
                        string childBody = await childCatalog.Content.ReadAsStringAsync();
                        Assert.Contains("echo", childBody);
                        Assert.DoesNotContain("Do not look it up.", childBody);
                    }
                    using (var content = new StringContent(
                        "{\"name\":\"task\",\"argumentsJson\":\"{\\\"description\\\":\\\"Tiles\\\",\\\"prompt\\\":\\\"look\\\"}\"}",
                        Encoding.UTF8,
                        "application/json"))
                    using (HttpResponseMessage denied = await childHttp.PostAsync(bridge.BaseUrl + "invoke", content))
                    {
                        denied.EnsureSuccessStatusCode();
                        string deniedBody = await denied.Content.ReadAsStringAsync();
                        Assert.Contains("cannot open another", deniedBody);
                    }
                }
                using (var bare = new HttpClient())
                using (HttpResponseMessage missing = await bare.GetAsync(bridge.BaseUrl + "tools"))
                {
                    Assert.Equal(System.Net.HttpStatusCode.Unauthorized, missing.StatusCode);
                }
            }
        }

        [Fact]
        public async Task Bridge_offers_the_preview_and_keeps_the_png_for_the_model()
        {
            byte[] jpeg = { 9, 9, 9 };
            byte[] png = { 104, 105 };
            var latch = new ToolPreviewLatch(null);
            var tools = new EchoTools();
            var book = new ConversationBook(
                (id, message, cancellationToken) => Task.FromResult("done"),
                _ => { },
                _ => { });
            using (ToolBridge bridge = ToolBridge.Start(tools, () => false, "Be the mayor.", book, latch))
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bridge.Token);
                using (var content = new StringContent(
                    "{\"name\":\"echo\",\"argumentsJson\":\"{\\\"zoom\\\":1}\"}",
                    Encoding.UTF8,
                    "application/json"))
                using (HttpResponseMessage invoked = await http.PostAsync(bridge.BaseUrl + "invoke", content))
                {
                    invoked.EnsureSuccessStatusCode();
                    string body = await invoked.Content.ReadAsStringAsync();
                    Assert.Contains(Convert.ToBase64String(png), body);
                    Assert.DoesNotContain(ToolPreview.DataUri(jpeg), body);
                }
            }
            string claimed = latch.Claim("call-1", "city_echo", "{\"zoom\":1}");
            Assert.Equal(ToolPreview.DataUri(jpeg), claimed);
        }

        [Fact]
        public async Task Cancel_stops_the_tool_call_in_flight()
        {
            var tools = new HoldTools();
            var book = new ConversationBook(
                (id, message, cancellationToken) => Task.FromResult("done"),
                _ => { },
                _ => { });
            using (ToolBridge bridge = ToolBridge.Start(tools, () => false, "Be the mayor.", book))
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bridge.Token);
                using (var request = new HttpRequestMessage(HttpMethod.Post, bridge.BaseUrl + "invoke"))
                {
                    request.Content = new StringContent(
                        "{\"name\":\"hold\",\"argumentsJson\":\"{}\"}",
                        Encoding.UTF8,
                        "application/json");
                    request.Headers.TryAddWithoutValidation("X-Call-Id", "hold-1");
                    Task<HttpResponseMessage> post = http.SendAsync(request);
                    Assert.True(await Task.WhenAny(tools.Started.Task, Task.Delay(5000)) == tools.Started.Task);
                    using (HttpResponseMessage cancel = await http.PostAsync(bridge.BaseUrl + "cancel?id=hold-1", new StringContent("")))
                    {
                        cancel.EnsureSuccessStatusCode();
                    }
                    bool cancelled = await Task.WhenAny(tools.Cancelled.Task, Task.Delay(5000)) == tools.Cancelled.Task;
                    Assert.True(cancelled);
                    (await post).Dispose();
                }
            }
        }

        [Fact]
        public async Task External_city_tool_calls_are_recorded_under_the_tool_server_call_id()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "airimayor-record-" + Guid.NewGuid().ToString("N"));
            try
            {
                string directory;
                using (var record = new SessionRecord("acp", new SessionRecordOptions { Root = root }, null))
                using (ToolBridge bridge = ToolBridge.Start(new EchoTools(), () => false, "Be the mayor.", null, null, record))
                using (var http = new HttpClient())
                {
                    directory = record.Directory;
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bridge.Token);
                    using (var request = new HttpRequestMessage(HttpMethod.Post, bridge.BaseUrl + "invoke"))
                    {
                        request.Headers.Add("X-Call-Id", "mcp-7");
                        request.Content = new StringContent(
                            "{\"name\":\"echo\",\"argumentsJson\":\"{}\"}", Encoding.UTF8, "application/json");
                        using (HttpResponseMessage invoked = await http.SendAsync(request))
                        {
                            invoked.EnsureSuccessStatusCode();
                        }
                    }
                }

                string[] lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(directory, SessionRecord.TimelineFile));
                Assert.Equal(2, lines.Length);
                using (JsonDocument start = JsonDocument.Parse(lines[0]))
                using (JsonDocument finish = JsonDocument.Parse(lines[1]))
                {
                    Assert.Equal("function.start", start.RootElement.GetProperty("type").GetString());
                    Assert.Equal("mcp-7", finish.RootElement.GetProperty("call").GetString());
                    Assert.Equal("calls/mcp-7/image.png", finish.RootElement.GetProperty("data").GetProperty("files")[0].GetString());
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

        private sealed class EchoTools : IAgentTools
        {
            public IReadOnlyList<AgentToolDeclaration> List(bool visionAvailable)
            {
                return new[]
                {
                    new AgentToolDeclaration
                    {
                        Name = "echo",
                        Description = "Echo",
                        ParametersJson = "{\"type\":\"object\"}",
                    },
                };
            }

            public Task<AgentToolResult> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken)
            {
                return Task.FromResult(new AgentToolResult
                {
                    Success = true,
                    Text = argumentsJson,
                    ImagePng = new byte[] { 104, 105 },
                    PreviewJpeg = new byte[] { 9, 9, 9 },
                });
            }
        }

        private sealed class HoldTools : IAgentTools
        {
            public TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>();
            public TaskCompletionSource<bool> Cancelled = new TaskCompletionSource<bool>();

            public IReadOnlyList<AgentToolDeclaration> List(bool visionAvailable)
            {
                return new[]
                {
                    new AgentToolDeclaration
                    {
                        Name = "hold",
                        Description = "Hold",
                        ParametersJson = "{\"type\":\"object\"}",
                    },
                };
            }

            public async Task<AgentToolResult> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken)
            {
                Started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult(true);
                    throw;
                }
                return new AgentToolResult { Success = true, Text = "{}" };
            }
        }
    }
}
