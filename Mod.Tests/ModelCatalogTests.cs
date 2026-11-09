using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using airimayor.Agent;
using Xunit;

namespace airimayor.Tests
{
    public sealed class ModelCatalogTests
    {
        internal const string Catalog = """
            {"new-cloud-provider":{"id":"new-cloud-provider","name":"A new cloud provider","env":["NEW_KEY"],"models":{
              "responses-model":{"id":"responses-model","name":"Responses model","type":"chat",
                "capabilities":{"tools":{"supported":true}},"modalities":{"input":["text","image/png"],"output":["text"]},
                "limit":{"context":256000,"output":32000},"api":{"responses":{"base_url":"https://cloud.test/openai/v1"}}},
              "chat-model":{"id":"chat-model","name":"Chat model","type":"chat",
                "capabilities":{"tools":{"supported":true}},"modalities":{"input":["text"],"output":["text"]},
                "limit":{"context":128000,"output":8000},"api":{"chat-completions":{"base_url":"https://cloud.test/chat/v1",
                  "compatibility":{"reasoning_field":"reasoning_content","max_tokens_field":"max_tokens"}}}},
              "messages-model":{"id":"messages-model","name":"Messages model","type":"chat",
                "capabilities":{"tools":{"supported":true}},"modalities":{"input":["text","image"],"output":["text"]},
                "limit":{"context":200000,"output":16000},"api":{"messages":{"base_url":"https://cloud.test/anthropic/v1"}}}
            }}}
            """;

        [Theory]
        [InlineData("responses-model", ModelProtocol.Responses, "https://cloud.test/openai/v1", 128000, 16384, true)]
        [InlineData("chat-model", ModelProtocol.ChatCompletions, "https://cloud.test/chat/v1", 128000, 8000, false)]
        [InlineData("messages-model", ModelProtocol.AnthropicMessages, "https://cloud.test/anthropic/v1", 128000, 16000, true)]
        public void A_cloud_provider_routes_each_model_without_a_provider_preset(string model, ModelProtocol protocol,
            string address, int context, int output, bool images)
        {
            ModelCatalogSnapshot catalog = ModelCatalogSnapshot.Parse(Catalog);
            Assert.Equal("A new cloud provider", Assert.Single(catalog.Providers).Name);
            ProviderConnection draft = new ProviderConnections().Read("new-cloud-provider").With(model: model, apiKey: "test-key");
            ResolvedModelConnection selected = catalog.Resolve(draft);
            Assert.Equal(protocol, selected.Route.Protocol);
            Assert.Equal(address, selected.Route.Endpoint);
            Assert.Equal(context, selected.WindowTokens);
            Assert.Equal(output, selected.OutputTokens);
            Assert.Equal(images, selected.Images);
            Assert.Empty(draft.Endpoint);
            Assert.Equal(ModelProtocol.Automatic, draft.Protocol);
            Assert.Equal(0, draft.WindowTokens);
        }

        [Fact]
        public void Explicit_overrides_and_bound_credentials_are_resolved_without_mutating_the_selection()
        {
            ModelCatalogSnapshot catalog = ModelCatalogSnapshot.Parse(Catalog);
            var profiles = new ProviderConnections();
            ProviderConnection draft = profiles.Read("new-cloud-provider").With(model: "chat-model", apiKey: "key",
                endpoint: "https://my-proxy.test/v1", windowTokens: 64000, credentialOrigin: "https://my-proxy.test");
            ResolvedModelConnection selected = catalog.Resolve(draft);
            Assert.Equal("https://my-proxy.test/v1", selected.Route.Endpoint);
            Assert.Equal(64000, selected.WindowTokens);
            Assert.Equal("reasoning_content", selected.Route.ReasoningField);
            Assert.Throws<InvalidOperationException>(() => catalog.Resolve(draft.With(credentialOrigin: "https://old-host.test")));
            Assert.Throws<InvalidOperationException>(() => catalog.Resolve(draft.With(windowTokens: 256000)));
            Assert.Throws<InvalidOperationException>(() => catalog.Resolve(draft.With(protocol: ModelProtocol.Responses, endpoint: "")));
        }

        [Theory]
        [InlineData(64000, 64000)]
        [InlineData(128000, 128000)]
        [InlineData(1000000, 128000)]
        public void Default_context_is_capped_at_128k_but_manual_budgets_can_use_the_full_model_window(int maximum, int defaultBudget)
        {
            JsonNode root = JsonNode.Parse(Catalog);
            root["new-cloud-provider"]["models"]["responses-model"]["limit"]["context"] = maximum;
            ModelCatalogSnapshot catalog = ModelCatalogSnapshot.Parse(root.ToJsonString());
            ProviderConnection draft = new ProviderConnections().Read("new-cloud-provider").With(model: "responses-model");
            Assert.Equal(defaultBudget, catalog.Resolve(draft).WindowTokens);
            Assert.Equal(maximum, catalog.Resolve(draft.With(windowTokens: maximum)).WindowTokens);
            Assert.Throws<InvalidOperationException>(() => catalog.Resolve(draft.With(windowTokens: maximum + 1)));
        }

        [Fact]
        public void Multiple_routes_have_a_stable_preference_and_allow_an_explicit_protocol()
        {
            JsonNode root = JsonNode.Parse(Catalog);
            JsonObject routes = root["new-cloud-provider"]["models"]["messages-model"]["api"].AsObject();
            routes["chat-completions"] = JsonNode.Parse("{\"base_url\":\"https://cloud.test/compat/v1\"}");
            ProviderConnection draft = new ProviderConnections().Read("new-cloud-provider").With(model: "messages-model");
            Assert.Equal(ModelProtocol.AnthropicMessages, ModelCatalogSnapshot.Parse(root.ToJsonString()).Resolve(draft).Route.Protocol);
            routes["responses"] = JsonNode.Parse("{\"base_url\":\"https://cloud.test/responses/v1\"}");
            ModelCatalogSnapshot catalog = ModelCatalogSnapshot.Parse(root.ToJsonString());
            Assert.Equal(ModelProtocol.Responses, catalog.Resolve(draft).Route.Protocol);
            Assert.Equal("https://cloud.test/compat/v1", catalog.Resolve(draft.With(protocol: ModelProtocol.ChatCompletions)).Route.Endpoint);
        }

        [Fact]
        public void Only_executable_text_chat_routes_with_city_tools_are_offered()
        {
            JsonNode root = JsonNode.Parse(Catalog);
            JsonObject models = root["new-cloud-provider"]["models"].AsObject();
            foreach (string id in new[] { "no-tools", "image-only", "google-only", "templated", "custom-path", "unknown-compatibility" })
            {
                JsonNode model = models["responses-model"].DeepClone();
                model["id"] = id;
                models[id] = model;
            }
            models["no-tools"]["capabilities"]["tools"]["supported"] = false;
            models["image-only"]["type"] = "image";
            models["google-only"]["api"] = JsonNode.Parse("{\"generate-content\":{\"base_url\":\"https://google.test/v1beta\"}}");
            models["templated"]["api"]["responses"]["base_url"] = "https://${HOST}/v1";
            models["custom-path"]["api"]["responses"]["path"] = "/different";
            models["unknown-compatibility"]["api"]["responses"]["compatibility"] = JsonNode.Parse("{\"new-required-option\":true}");
            ModelProvider provider = Assert.Single(ModelCatalogSnapshot.Parse(root.ToJsonString()).Providers);
            Assert.Equal(3, provider.Models.Count);
        }

        [Fact]
        public void Missing_models_preserve_the_id_and_require_explicit_configuration()
        {
            var profiles = new ProviderConnections();
            ProviderConnection draft = profiles.Read("removed-provider").With(model: "manual-model", apiKey: "test-key");
            profiles.Set(draft);
            ProviderConnections restored = ProviderConnections.FromJson(profiles.ToJson());
            Assert.Equal("manual-model", restored.Read("removed-provider").Model);
            Assert.Throws<InvalidOperationException>(() => ModelCatalogSnapshot.Empty.Resolve(draft));
            ResolvedModelConnection manual = ModelCatalogSnapshot.Empty.Resolve(draft.With(endpoint: "https://manual.test/v1", protocol: ModelProtocol.ChatCompletions));
            Assert.Equal("manual-model", manual.Settings.Model);
            ProviderConnection custom = profiles.Read(ModelProviders.Compatible).With(model: "local-model", endpoint: "http://localhost:11434/v1");
            Assert.False(ModelCatalogSnapshot.Empty.Resolve(custom).RequiresKey);
        }

        [Fact]
        public async Task Refresh_and_restart_keep_a_valid_catalog_and_never_change_an_applied_route()
        {
            await WithCacheAsync(async path =>
            {
                var handler = new CatalogHandler(Response(Catalog, "\"one\""), Response(Catalog.Replace("cloud.test", "new-cloud.test"), "\"two\""),
                    new HttpResponseMessage(HttpStatusCode.NotModified), Response("not-json", "\"bad\""));
                using var http = new HttpClient(handler);
                var catalog = new ModelCatalog(path, http);
                catalog.Load();
                Assert.Empty(catalog.Current.Providers);
                await catalog.RefreshAsync();
                ModelCatalogSnapshot original = catalog.Current;
                ProviderConnection draft = new ProviderConnections().Read("new-cloud-provider").With(model: "responses-model", apiKey: "test-key", credentialOrigin: "https://cloud.test");
                ResolvedModelConnection applied = original.Resolve(draft);
                await catalog.RefreshAsync();
                Assert.Equal("https://cloud.test/openai/v1", applied.Route.Endpoint);
                Assert.Throws<InvalidOperationException>(() => catalog.Current.Resolve(draft));
                Assert.Equal("\"one\"", handler.ETags[1]);
                ModelCatalogSnapshot updated = catalog.Current;
                await catalog.RefreshAsync();
                Assert.Same(updated, catalog.Current);
                await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => catalog.RefreshAsync());
                Assert.Same(updated, catalog.Current);
                var restarted = new ModelCatalog(path, http);
                restarted.Load();
                Assert.StartsWith("https://new-cloud.test", restarted.Current.Resolve(draft.With(apiKey: "", credentialOrigin: "")).Route.Endpoint);
                Assert.DoesNotContain("test-key", File.ReadAllText(path));
                Assert.All(handler.Authorizations, authorization => Assert.Null(authorization));
            });
        }

        [Fact]
        public async Task An_HTTP_failure_or_unknown_cache_shape_does_not_replace_good_data()
        {
            await WithCacheAsync(async path =>
            {
                using var http = new HttpClient(new CatalogHandler(Response(Catalog, "\"ok\""), new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
                var catalog = new ModelCatalog(path, http);
                await catalog.RefreshAsync();
                string saved = File.ReadAllText(path);
                await Assert.ThrowsAsync<HttpRequestException>(() => catalog.RefreshAsync());
                Assert.Equal(saved, File.ReadAllText(path));
                File.WriteAllText(path, "{\"schema\":99,\"catalog\":{},\"etag\":\"\"}");
                Assert.Throws<InvalidDataException>(() => new ModelCatalog(path, http).Load());
            });
        }

        private static HttpResponseMessage Response(string json, string etag)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            response.Headers.ETag = new EntityTagHeaderValue(etag);
            return response;
        }

        private static async Task WithCacheAsync(Func<string, Task> action)
        {
            string path = Path.Combine(Path.GetTempPath(), "airimayor-catalog-test-" + Guid.NewGuid().ToString("N") + ".json");
            try { await action(path); }
            finally { File.Delete(path); File.Delete(path + ".tmp"); }
        }

        private sealed class CatalogHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> m_Replies;
            public readonly List<string> ETags = new List<string>();
            public readonly List<string> Authorizations = new List<string>();
            public CatalogHandler(params HttpResponseMessage[] replies) => m_Replies = new Queue<HttpResponseMessage>(replies);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(ModelCatalog.Endpoint, request.RequestUri.AbsoluteUri);
                ETags.Add(request.Headers.IfNoneMatch.FirstOrDefault()?.ToString());
                Authorizations.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(m_Replies.Dequeue());
            }
        }
    }
}
