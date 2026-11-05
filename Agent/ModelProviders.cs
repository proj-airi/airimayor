using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace airimayor.Agent
{
    public sealed class ModelProvider
    {
        public string Id { get; }
        public string Name { get; }
        private readonly string m_Endpoint;
        private readonly string m_ModelsEndpoint;
        private readonly ModelProtocol m_Protocol;
        internal bool PublicCatalog { get; }
        internal bool RequiresKey { get; }
        internal bool RequiresSession { get; }

        internal ModelProvider(string id, string name, string endpoint, string modelsEndpoint, ModelProtocol protocol,
            bool publicCatalog = false, bool optionalKey = false, bool requiresSession = false)
        {
            Id = id;
            Name = name;
            m_Endpoint = endpoint;
            m_ModelsEndpoint = modelsEndpoint;
            m_Protocol = protocol;
            PublicCatalog = publicCatalog;
            RequiresKey = !optionalKey;
            RequiresSession = requiresSession;
        }

        public ProviderConnection DefaultConnection() =>
            new ProviderConnection(Id, m_Endpoint, m_ModelsEndpoint, "", "", m_Protocol, 128_000);

        public IReadOnlyList<ModelProtocol> Protocols => m_Protocol == ModelProtocol.AnthropicMessages
            ? new[] { ModelProtocol.AnthropicMessages } : new[] { ModelProtocol.Responses, ModelProtocol.ChatCompletions };
        internal bool Allows(ModelProtocol protocol) => Protocols.Contains(protocol);
    }

    public sealed class ProviderModel
    {
        public string Id { get; }
        public string Name { get; }
        public int? ContextTokens { get; }
        public bool? Tools { get; }
        public bool? Images { get; }

        internal ProviderModel(string id, string name, int? contextTokens, bool? tools, bool? images)
        {
            Id = id;
            Name = name;
            ContextTokens = contextTokens;
            Tools = tools;
            Images = images;
        }
    }

    /// <summary>Provider discovery and SDK construction behind the built-in mayor's IChatClient seam.</summary>
    public static class ModelProviders
    {
        public const string OpenAi = "openai";
        public const string Go = "opencode-go";
        public const string OpenRouter = "openrouter";
        public const string Anthropic = "anthropic";
        public const string Compatible = "compatible";
        private static readonly string s_Application = "airimayor/" + typeof(ModelProviders).Assembly.GetName().Version;
        private static readonly HttpClient s_CatalogHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public static IReadOnlyList<ModelProvider> All { get; } = new[]
        {
            new ModelProvider(OpenAi, "OpenAI", "https://api.openai.com/v1", "https://api.openai.com/v1/models", ModelProtocol.Responses),
            new ModelProvider(Go, "OpenCode Go", "https://opencode.ai/inference/go/openai/v1", "https://opencode.ai/inference/go/v1/models", ModelProtocol.Responses, publicCatalog: true, requiresSession: true),
            new ModelProvider(OpenRouter, "OpenRouter", "https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/models", ModelProtocol.ChatCompletions, publicCatalog: true),
            new ModelProvider(Anthropic, "Anthropic", "https://api.anthropic.com", "https://api.anthropic.com/v1/models", ModelProtocol.AnthropicMessages),
            new ModelProvider(Compatible, "OpenAI-compatible", "", "", ModelProtocol.ChatCompletions, optionalKey: true),
        };

        public static ModelProvider Find(string id) => All.FirstOrDefault(provider => provider.Id == id)
            ?? throw new InvalidDataException("Unknown model provider: " + id + ".");

        public static async Task<IReadOnlyList<ProviderModel>> LoadModelsAsync(
            ProviderConnection connection, CancellationToken cancellationToken = default, HttpClient http = null)
        {
            ModelProvider provider = Find(connection.ProviderId);
            Uri inference = Address(connection.Endpoint);
            Uri address = Address(string.IsNullOrWhiteSpace(connection.ModelsEndpoint)
                ? connection.Endpoint.TrimEnd('/') + "/models" : connection.ModelsEndpoint);
            bool authenticated = !provider.PublicCatalog && !string.IsNullOrWhiteSpace(connection.ApiKey);
            if (authenticated && inference.GetLeftPart(UriPartial.Authority) != address.GetLeftPart(UriPartial.Authority))
                throw new InvalidOperationException("The model list and server must share an origin before an API key can be sent.");
            if (provider.RequiresKey && !provider.PublicCatalog && !authenticated)
                throw new InvalidOperationException("Set an API key before loading this provider's models.");

            var models = new Dictionary<string, ProviderModel>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string next = address.AbsoluteUri;
            do
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, next))
                {
                    request.Headers.UserAgent.ParseAdd(s_Application);
                    if (authenticated)
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
                        if (connection.Protocol == ModelProtocol.AnthropicMessages)
                            request.Headers.Add("anthropic-version", "2023-06-01");
                    }
                    using (HttpResponseMessage response = await (http ?? s_CatalogHttp).SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException("Model list returned HTTP " + (int)response.StatusCode + ". " + Error(json, connection.ApiKey));
                        JsonNode root = JsonNode.Parse(json);
                        if (!(root?["data"] is JsonArray entries))
                            throw new InvalidDataException("The provider returned an unsupported model list.");
                        foreach (JsonNode entry in entries)
                        {
                            string id = entry["id"]?.GetValue<string>();
                            string type = entry["type"]?.GetValue<string>();
                            if (string.IsNullOrWhiteSpace(id) || (type != null && type != "chat" && type != "model"))
                                continue;
                            JsonArray modalities = entry["modalities"]?["input"] as JsonArray;
                            models[id] = new ProviderModel(id,
                                entry["name"]?.GetValue<string>() ?? entry["display_name"]?.GetValue<string>() ?? id,
                                entry["limit"]?["context"]?.GetValue<int>() ?? entry["context_length"]?.GetValue<int>(),
                                entry["capabilities"]?["tools"]?["supported"]?.GetValue<bool>(),
                                modalities == null ? (bool?)null : modalities.Any(value => value.GetValue<string>().StartsWith("image", StringComparison.Ordinal)));
                        }
                        next = null;
                        if (root["has_more"]?.GetValue<bool>() == true)
                        {
                            string cursor = root["last_id"]?.GetValue<string>();
                            if (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor))
                                throw new InvalidDataException("The provider returned an invalid model-list cursor.");
                            next = address.AbsoluteUri + (string.IsNullOrEmpty(address.Query) ? "?" : "&") + "after_id=" + Uri.EscapeDataString(cursor);
                        }
                    }
                }
            } while (next != null);
            if (models.Count == 0)
                throw new InvalidDataException("The provider returned no chat models. You can enter a model ID manually.");
            return models.Values.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static IChatClient CreateChatClient(ProviderConnection connection, string conversationId,
            HttpMessageHandler transport = null)
        {
            ModelProvider provider = Find(connection.ProviderId);
            Address(connection.Endpoint);
            if (string.IsNullOrWhiteSpace(connection.Model))
                throw new InvalidOperationException("Choose or enter a model, then apply settings.");
            if (string.IsNullOrWhiteSpace(connection.ApiKey) && provider.RequiresKey)
                throw new InvalidOperationException("Set this provider's API key, then apply settings.");
            if (!provider.Allows(connection.Protocol))
                throw new InvalidOperationException("Choose a supported model protocol.");
            if (provider.RequiresSession && string.IsNullOrWhiteSpace(conversationId))
                throw new InvalidOperationException("A conversation ID is required for OpenCode Go.");

            var http = new HttpClient(new ApplicationHeaders(provider.RequiresSession ? conversationId : null,
                transport ?? new HttpClientHandler { AllowAutoRedirect = false })) { Timeout = Timeout.InfiniteTimeSpan };
            try
            {
                if (connection.Protocol == ModelProtocol.AnthropicMessages)
                {
                    var sdk = new AnthropicClient
                    {
                        ApiKey = connection.ApiKey,
                        BaseUrl = connection.Endpoint.TrimEnd('/'),
                        HttpClient = http,
                        Timeout = TimeSpan.FromMinutes(10),
                    };
                    return new OwnedChatClient(sdk.AsIChatClient(connection.Model, defaultMaxOutputTokens: 16_384), sdk);
                }
                var options = new OpenAIClientOptions
                {
                    Endpoint = new Uri(connection.Endpoint),
                    Transport = new HttpClientPipelineTransport(http),
                    NetworkTimeout = TimeSpan.FromMinutes(10),
                    UserAgentApplicationId = s_Application,
                };
                var openai = string.IsNullOrWhiteSpace(connection.ApiKey)
                    ? new AnonymousOpenAIClient(options)
                    : new OpenAIClient(new ApiKeyCredential(connection.ApiKey), options);
                IChatClient client = connection.Protocol == ModelProtocol.Responses
                    ? openai.GetResponsesClient().AsIChatClient(connection.Model)
                    : openai.GetChatClient(connection.Model).AsIChatClient();
                return new OwnedChatClient(client, http);
            }
            catch
            {
                http.Dispose();
                throw;
            }
        }

        private static Uri Address(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("Enter a valid server or model-list address.");
            return uri;
        }

        private static string Error(string json, string apiKey)
        {
            string text = "";
            try
            {
                JsonNode error = (JsonNode.Parse(json) as JsonObject)?["error"];
                JsonValue message = error is JsonObject details ? details["message"] as JsonValue : error as JsonValue;
                if (message != null && message.TryGetValue(out string detail)) text = detail;
            }
            catch (System.Text.Json.JsonException)
            {
                text = "The provider did not return a JSON error.";
            }
            if (!string.IsNullOrEmpty(apiKey)) text = text.Replace(apiKey, "[redacted]");
            return SessionRecord.RedactSecrets(text.Length > 500 ? text.Substring(0, 500) : text);
        }

        private sealed class ApplicationHeaders : DelegatingHandler
        {
            private readonly string m_Conversation;
            public ApplicationHeaders(string conversation, HttpMessageHandler inner) : base(inner) => m_Conversation = conversation;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.Remove("User-Agent");
                request.Headers.UserAgent.ParseAdd(s_Application);
                if (m_Conversation != null)
                {
                    request.Headers.Remove("x-opencode-session");
                    request.Headers.Add("x-opencode-session", m_Conversation);
                }
                return base.SendAsync(request, cancellationToken);
            }
        }

        private sealed class OwnedChatClient : DelegatingChatClient
        {
            private readonly IDisposable m_Owner;
            public OwnedChatClient(IChatClient client, IDisposable owner) : base(client) => m_Owner = owner;
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing) m_Owner.Dispose();
            }
        }

        private sealed class AnonymousOpenAIClient : OpenAIClient
        {
            public AnonymousOpenAIClient(OpenAIClientOptions options) : base(ClientPipeline.Create(options), options) { }
        }
    }
}
