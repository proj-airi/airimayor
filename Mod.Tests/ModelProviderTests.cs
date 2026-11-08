using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using airimayor.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace airimayor.Tests
{
    public sealed class ModelProviderTests
    {
        [Fact]
        public void Provider_switching_and_serialization_preserve_independent_connections()
        {
            var profiles = new ProviderConnections();
            ProviderConnection openai = profiles.Read("openai").With(apiKey: "openai-test-key", model: "gpt-test", windowTokens: 256_000);
            ProviderConnection go = profiles.Read(ModelProviders.Go).With(apiKey: "go-test-key", model: "kimi-test");
            profiles.Set(openai);
            profiles.Set(go);
            profiles.Set(openai.With(model: "next-model"));
            Assert.Equal("gpt-test", openai.Model);
            Assert.Equal("kimi-test", profiles.Read(ModelProviders.Go).Model);
            ProviderConnections restored = ProviderConnections.FromJson(profiles.ToJson());
            Assert.Equal("next-model", restored.Read("openai").Model);
            Assert.Equal("openai-test-key", restored.Read("openai").ApiKey);
            Assert.Equal(256_000, restored.Read("openai").WindowTokens);
            Assert.Equal("go-test-key", restored.Read(ModelProviders.Go).ApiKey);
        }

        [Fact]
        public void Changing_a_host_clears_its_existing_key_without_touching_other_providers()
        {
            var profiles = new ProviderConnections();
            ProviderConnection original = profiles.Read(ModelProviders.Compatible).With(endpoint: "https://first.test/v1", apiKey: "first-key");
            profiles.Set(original);
            profiles.Set(original.With(endpoint: "https://first.test/another/v1"));
            Assert.Equal("first-key", profiles.Read(ModelProviders.Compatible).ApiKey);
            profiles.Set(original.With(endpoint: "https://second.test/v1"));
            Assert.Empty(profiles.Read(ModelProviders.Compatible).ApiKey);
            Assert.Equal("first-key", original.ApiKey);
            profiles.Set(original.With(endpoint: "https://third.test/v1", apiKey: "third-key"));
            Assert.Equal("third-key", profiles.Read(ModelProviders.Compatible).ApiKey);
        }

        [Theory]
        [InlineData("{\"schema\":3,\"connections\":[]}")]
        [InlineData("{\"schema\":1,\"connections\":[],\"oldEndpoint\":\"https://old.test\"}")]
        [InlineData("{\"schema\":1}")]
        public void Unknown_settings_shapes_are_rejected(string json)
        {
            Assert.Throws<InvalidDataException>(() => ProviderConnections.FromJson(json));
        }

        [Theory]
        [InlineData("0")]
        [InlineData("Unknown")]
        [InlineData("999")]
        public void Unsupported_saved_protocols_are_rejected(string protocol)
        {
            var profiles = new ProviderConnections();
            profiles.Set(profiles.Read("openai"));
            JsonNode saved = JsonNode.Parse(profiles.ToJson())!;
            saved["connections"]![0]!["protocol"] = protocol;
            Assert.Throws<InvalidDataException>(() => ProviderConnections.FromJson(saved.ToJsonString()));
        }

        [Fact]
        public async Task Catalogs_stay_with_their_source_and_preserve_a_manual_model()
        {
            var profiles = new ProviderConnections();
            ProviderConnection source = profiles.Read(ModelProviders.Go).With(endpoint: "https://provider.test/v1", apiKey: "old-key", model: "manually-entered");
            profiles.Set(source);
            using var http = new HttpClient(new ReplyHandler("{\"data\":[{\"id\":\"listed-model\"}]}"));
            IReadOnlyList<ProviderModel> models = await ModelProviders.LoadModelsAsync(source, http: http);
            Assert.True(profiles.AcceptModels(source, models));
            Assert.Single(profiles.Models(ModelProviders.Go));
            Assert.Empty(profiles.Models("openai"));
            Assert.Equal("manually-entered", profiles.Read(ModelProviders.Go).Model);
            profiles.Set(source.With(model: "another-manual-model"));
            Assert.Single(profiles.Models(ModelProviders.Go));
            profiles.Set(source.With(apiKey: "new-key"));
            Assert.False(profiles.AcceptModels(source, models));
            Assert.Empty(profiles.Models(ModelProviders.Go));
        }

        [Fact]
        public async Task Go_discovers_models_separately_without_sending_the_inference_key()
        {
            var handler = new ReplyHandler("""
                {"data":[{"id":"model-a","name":"Model A","type":"chat","limit":{"context":256000},
                "modalities":{"input":["text","image/png"]},"capabilities":{"tools":{"supported":true}}},
                {"id":"image-model","type":"image"}]}
                """);
            using var http = new HttpClient(handler);
            ProviderConnection go = new ProviderConnections().Read(ModelProviders.Go).With(endpoint: "https://opencode.ai/inference/go/openai/v1", apiKey: "go-test-key");
            IReadOnlyList<ProviderModel> models = await ModelProviders.LoadModelsAsync(go, http: http);
            ProviderModel model = Assert.Single(models);
            Assert.Equal("/inference/go/v1/models", Assert.Single(handler.Requests).Path);
            Assert.Null(handler.Requests[0].Authorization);
            Assert.Equal(256_000, model.ContextTokens);
            Assert.True(model.Tools);
            Assert.True(model.Images);
        }

        [Fact]
        public async Task Anthropic_model_discovery_uses_native_authentication_and_pagination()
        {
            var handler = new ReplyHandler("{\"data\":[{\"id\":\"first\",\"display_name\":\"First\"}],\"has_more\":true,\"last_id\":\"first\"}",
                "{\"data\":[{\"id\":\"second\"}],\"has_more\":false}");
            using var http = new HttpClient(handler);
            ProviderConnection connection = new ProviderConnections().Read("anthropic").With(endpoint: "https://api.anthropic.com/v1", protocol: ModelProtocol.AnthropicMessages, apiKey: "anthropic-test-key");
            IReadOnlyList<ProviderModel> models = await ModelProviders.LoadModelsAsync(connection, http: http);
            Assert.Equal(2, models.Count);
            Assert.Equal("Bearer anthropic-test-key", handler.Requests[0].Authorization);
            Assert.Equal("?after_id=first", handler.Requests[1].Query);
        }

        [Fact]
        public async Task Catalog_authentication_cannot_send_a_key_to_another_origin()
        {
            var handler = new ReplyHandler("{\"data\":[]}");
            using var http = new HttpClient(handler);
            ProviderConnection connection = new ProviderConnections().Read("openai").With(endpoint: "https://provider.test/v1")
                .With(apiKey: "test-key", modelsEndpoint: "https://another.test/models");
            await Assert.ThrowsAsync<InvalidOperationException>(() => ModelProviders.LoadModelsAsync(connection, http: http));
            Assert.Empty(handler.Requests);
        }

        [Theory]
        [InlineData("{\"error\":{\"message\":\"Rejected secret-value\"}}")]
        [InlineData("{\"error\":\"Rejected secret-value\"}")]
        [InlineData("{\"error\":401}")]
        [InlineData("<html>Unauthorized</html>")]
        public async Task Catalog_failure_keeps_HTTP_status_and_redacts_the_key(string body)
        {
            var handler = new ReplyHandler(body) { Status = HttpStatusCode.Unauthorized };
            using var http = new HttpClient(handler);
            ProviderConnection connection = new ProviderConnections().Read("openai").With(endpoint: "https://provider.test/v1").With(apiKey: "secret-value");
            Exception error = await Assert.ThrowsAsync<HttpRequestException>(() => ModelProviders.LoadModelsAsync(connection, http: http));
            Assert.Contains("HTTP 401", error.Message);
            Assert.DoesNotContain("secret-value", error.Message);
        }

        [Theory]
        [InlineData("openai", "/v1/responses", ModelProtocol.Responses)]
        [InlineData(ModelProviders.Go, "/inference/go/openai/v1/responses", ModelProtocol.Responses)]
        [InlineData("openrouter", "/api/v1/chat/completions", ModelProtocol.ChatCompletions)]
        [InlineData("anthropic", "/v1/messages", ModelProtocol.AnthropicMessages)]
        [InlineData(ModelProviders.Compatible, "/v1/chat/completions", ModelProtocol.ChatCompletions)]
        public async Task Sdk_adapters_use_the_selected_route_authentication_and_tools(string provider, string path, ModelProtocol protocol)
        {
            var handler = new ReplyHandler(Response(protocol));
            ProviderConnection connection = new ProviderConnections().Read(provider).With(model: "test-model", apiKey: "test-key", protocol: protocol, endpoint: "https://provider.test" + path.Substring(0, path.Length - (protocol == ModelProtocol.ChatCompletions ? "/chat/completions" : protocol == ModelProtocol.Responses ? "/responses" : "/messages").Length));
            if (provider == ModelProviders.Compatible)
                connection = connection.With(endpoint: "http://localhost:11434/v1", apiKey: "");
            using (IChatClient client = ModelProviders.CreateChatClient(ModelCatalogSnapshot.Empty.Resolve(connection), "conversation-123", handler))
            {
                ChatResponse response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "Ready?") }, Options());
                Assert.Equal("Ready.", response.Text);
            }
            Request request = Assert.Single(handler.Requests);
            Assert.Equal(path, request.Path);
            Assert.Contains("city_echo", request.Body);
            Assert.StartsWith("airimayor/", request.UserAgent);
            Assert.Equal(provider == ModelProviders.Go ? "conversation-123" : null, request.Session);
            Assert.Equal(provider == ModelProviders.Compatible ? null : "Bearer test-key", request.Authorization);
            Assert.True(handler.Disposed);
        }

        [Theory]
        [InlineData(ModelProtocol.Responses)]
        [InlineData(ModelProtocol.ChatCompletions)]
        [InlineData(ModelProtocol.AnthropicMessages)]
        public async Task Every_protocol_streams_text_through_IChatClient(ModelProtocol protocol)
        {
            var handler = new ReplyHandler(StreamResponse(protocol)) { Streaming = true };
            ProviderConnection connection = new ProviderConnections().Read("stream-provider")
                .With(endpoint: "https://provider.test/v1", model: "test-model", apiKey: "test-key", protocol: protocol);
            using IChatClient client = ModelProviders.CreateChatClient(ModelCatalogSnapshot.Empty.Resolve(connection), "stream-chat", handler);
            var text = new StringBuilder();
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "Ready?") }, Options()))
                text.Append(update.Text);
            Assert.Equal("Ready.", text.ToString());
            Assert.True(JsonNode.Parse(Assert.Single(handler.Requests).Body)["stream"].GetValue<bool>());
        }

        [Fact]
        public async Task Catalog_compatibility_keeps_reasoning_on_a_tool_round_trip_and_uses_the_declared_token_field()
        {
            var handler = new ReplyHandler("""
                {"id":"chatcmpl_tool","object":"chat.completion","created":0,"model":"chat-model",
                  "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":"",
                    "reasoning_content":"Inspect the city first.","tool_calls":[{"id":"city-call","type":"function",
                    "function":{"name":"city_echo","arguments":"{\"message\":\"ready\"}"}}]}}],
                  "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """, Response(ModelProtocol.ChatCompletions));
            ProviderConnection draft = new ProviderConnections().Read("new-cloud-provider").With(model: "chat-model", apiKey: "test-key");
            ResolvedModelConnection selected = ModelCatalogSnapshot.Parse(ModelCatalogTests.Catalog).Resolve(draft);
            using IChatClient client = ModelProviders.CreateChatClient(selected, "tool-chat", handler);
            var history = new List<ChatMessage> { new ChatMessage(ChatRole.User, "Inspect the city.") };
            ChatResponse first = await client.GetResponseAsync(history, Options());
            Assert.Equal("Inspect the city first.", Assert.Single(first.Messages[0].Contents.OfType<TextReasoningContent>()).Text);
            history.AddRange(first.Messages);
            history.Add(new ChatMessage(ChatRole.Tool, new[] { new FunctionResultContent("city-call", "Ready.") }));
            await client.GetResponseAsync(history, Options());
            JsonNode body = JsonNode.Parse(handler.Requests[1].Body);
            Assert.Equal("Inspect the city first.", body["messages"][1]["reasoning_content"].GetValue<string>());
            Assert.Equal("city-call", body["messages"][1]["tool_calls"][0]["id"].GetValue<string>());
            Assert.Equal(128, body["max_tokens"].GetValue<int>());
            Assert.Null(body["max_completion_tokens"]);
            Assert.NotNull(first.Messages[0].RawRepresentation);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Chat_completions_sends_city_tool_images_as_images_after_all_tool_results(bool streaming, bool separateResults)
        {
            var protocol = ModelProtocol.ChatCompletions;
            var handler = new ReplyHandler(streaming ? StreamResponse(protocol) : Response(protocol)) { Streaming = streaming };
            var connection = new ProviderConnections().Read(ModelProviders.Go)
                .With(endpoint: "https://provider.test/v1", model: "test-model", apiKey: "test-key", protocol: protocol);
            using IChatClient client = ModelProviders.CreateChatClient(ModelCatalogSnapshot.Empty.Resolve(connection), "image-chat", handler);
            var image = new DataContent(new byte[] { 1, 2, 3, 4 }, "image/png");
            var result = new FunctionResultContent("image-call", new AIContent[] { new TextContent("City map"), image });
            var tools = new ChatMessage(ChatRole.Tool, new AIContent[] { result, new FunctionResultContent("read-call", "Population 98") });
            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.Assistant, new AIContent[]
                {
                    new FunctionCallContent("image-call", "map_image", new Dictionary<string, object>()),
                    new FunctionCallContent("read-call", "get_statistics", new Dictionary<string, object>()),
                }),
                tools,
            };
            if (separateResults)
            {
                messages[1] = new ChatMessage(ChatRole.Tool, new AIContent[] { result });
                messages.Add(new ChatMessage(ChatRole.Tool, new AIContent[] { tools.Contents[1] }));
            }
            if (streaming)
                await client.GetStreamingResponseAsync(messages, Options()).ToChatResponseAsync();
            else
                await client.GetResponseAsync(messages, Options());
            var sent = JsonNode.Parse(Assert.Single(handler.Requests).Body)["messages"].AsArray();
            Assert.Equal(4, sent.Count);
            Assert.Equal("tool", (string)sent[1]["role"]);
            Assert.Equal("City map", (string)sent[1]["content"]);
            Assert.Equal("image-call", (string)sent[1]["tool_call_id"]);
            Assert.Equal("Population 98", (string)sent[2]["content"]);
            Assert.Equal("user", (string)sent[3]["role"]);
            var parts = sent[3]["content"].AsArray();
            Assert.Contains("image-call", (string)parts[0]["text"]);
            Assert.Equal("image_url", (string)parts[1]["type"]);
            Assert.Equal("data:image/png;base64,AQIDBA==", (string)parts[1]["image_url"]["url"]);
            Assert.Same(result, tools.Contents[0]);
            Assert.Same(image, Assert.IsType<AIContent[]>(result.Result)[1]);
        }

        private static ChatOptions Options() => new ChatOptions
        {
            MaxOutputTokens = 128,
            Tools = new[] { AIFunctionFactory.Create((string message) => message, "city_echo") },
        };

        private static string Response(ModelProtocol protocol) => protocol switch
        {
            ModelProtocol.Responses => """{"id":"resp_1","object":"response","created_at":0,"status":"completed","model":"test-model","output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Ready.","annotations":[]}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}""",
            ModelProtocol.ChatCompletions => """{"id":"chatcmpl_1","object":"chat.completion","created":0,"model":"test-model","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Ready."}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""",
            _ => """{"id":"msg_1","type":"message","role":"assistant","model":"test-model","content":[{"type":"text","text":"Ready."}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}""",
        };

        private static string StreamResponse(ModelProtocol protocol)
        {
            if (protocol == ModelProtocol.Responses)
                return "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":0,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"Ready.\"}\n\n" +
                    "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":1,\"response\":" + Response(protocol) + "}\n\n";
            if (protocol == ModelProtocol.ChatCompletions)
                return "data: {\"id\":\"chatcmpl_1\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Ready.\"},\"finish_reason\":null}]}\n\n" +
                    "data: {\"id\":\"chatcmpl_1\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return """
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"test-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Ready."}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

                event: message_stop
                data: {"type":"message_stop"}


                """;
        }

        private sealed class Request
        {
            public string Path, Query, Body, UserAgent, Session, Authorization, ApiKey;
        }

        private sealed class ReplyHandler : HttpMessageHandler
        {
            private readonly Queue<string> m_Replies;
            public readonly List<Request> Requests = new List<Request>();
            public HttpStatusCode Status = HttpStatusCode.OK;
            public bool Streaming;
            public bool Disposed;
            public ReplyHandler(params string[] replies) => m_Replies = new Queue<string>(replies);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(new Request
                {
                    Path = request.RequestUri.AbsolutePath,
                    Query = request.RequestUri.Query,
                    Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                    UserAgent = request.Headers.UserAgent.ToString(),
                    Session = Header(request, "x-opencode-session"),
                    Authorization = request.Headers.Authorization?.ToString(),
                    ApiKey = Header(request, "x-api-key"),
                });
                return new HttpResponseMessage(Status)
                {
                    Content = new StringContent(m_Replies.Dequeue(), Encoding.UTF8, Streaming ? "text/event-stream" : "application/json"),
                };
            }

            private static string Header(HttpRequestMessage request, string name) =>
                request.Headers.TryGetValues(name, out IEnumerable<string> values) ? values.Single() : null;

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
