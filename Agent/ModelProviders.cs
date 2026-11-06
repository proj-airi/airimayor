using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace airimayor.Agent
{
    /// <summary>Protocol SDK construction and optional live model checks behind IChatClient.</summary>
    public static class ModelProviders
    {
        public const string Go = "opencode-go";
        public const string Compatible = "compatible";
        internal static readonly string Application = "airimayor/" + typeof(ModelProviders).Assembly.GetName().Version;
        private static readonly HttpClient s_CatalogHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public static async Task<IReadOnlyList<ProviderModel>> LoadModelsAsync(
            ProviderConnection connection, CancellationToken cancellationToken = default, HttpClient http = null)
        {
            Uri inference = Address(connection.Endpoint);
            Uri address = Address(string.IsNullOrWhiteSpace(connection.ModelsEndpoint)
                ? connection.ProviderId == Go ? "https://opencode.ai/inference/go/v1/models" : connection.Endpoint.TrimEnd('/') + "/models"
                : connection.ModelsEndpoint);
            bool authenticated = connection.ProviderId != Go && !string.IsNullOrWhiteSpace(connection.ApiKey);
            if (authenticated && inference.GetLeftPart(UriPartial.Authority) != address.GetLeftPart(UriPartial.Authority))
                throw new InvalidOperationException("The model list and server must share an origin before an API key can be sent.");
            if (connection.ProviderId != Compatible && connection.ProviderId != Go && !authenticated)
                throw new InvalidOperationException("Set an API key before loading this provider's models.");

            var models = new Dictionary<string, ProviderModel>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string next = address.AbsoluteUri;
            do
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, next))
                {
                    request.Headers.UserAgent.ParseAdd(Application);
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

        public static IChatClient CreateChatClient(ResolvedModelConnection resolved, string conversationId,
            HttpMessageHandler transport = null)
        {
            ProviderConnection connection = resolved.Settings;
            ModelRoute route = resolved.Route;
            Address(route.Endpoint);
            if (string.IsNullOrWhiteSpace(connection.Model))
                throw new InvalidOperationException("Choose or enter a model, then apply settings.");
            if (string.IsNullOrWhiteSpace(connection.ApiKey) && resolved.RequiresKey)
                throw new InvalidOperationException("Set this provider's API key, then apply settings.");
            if (connection.ProviderId == Go && string.IsNullOrWhiteSpace(conversationId))
                throw new InvalidOperationException("A conversation ID is required for OpenCode Go.");

            var http = new HttpClient(new ApplicationHeaders(connection.ProviderId == Go ? conversationId : null, route.MaxTokensField,
                transport ?? new HttpClientHandler { AllowAutoRedirect = false })) { Timeout = Timeout.InfiniteTimeSpan };
            try
            {
                if (route.Protocol == ModelProtocol.AnthropicMessages)
                {
                    string endpoint = route.Endpoint.TrimEnd('/');
                    var sdk = new AnthropicClient
                    {
                        ApiKey = connection.ApiKey,
                        BaseUrl = endpoint.EndsWith("/v1", StringComparison.Ordinal) ? endpoint.Substring(0, endpoint.Length - 3) : endpoint,
                        HttpClient = http,
                        Timeout = TimeSpan.FromMinutes(10),
                    };
                    return new OwnedChatClient(sdk.AsIChatClient(connection.Model, defaultMaxOutputTokens: resolved.OutputTokens), sdk);
                }
                var options = new OpenAIClientOptions
                {
                    Endpoint = new Uri(route.Endpoint),
                    Transport = new HttpClientPipelineTransport(http),
                    NetworkTimeout = TimeSpan.FromMinutes(10),
                    UserAgentApplicationId = Application,
                };
                var openai = string.IsNullOrWhiteSpace(connection.ApiKey)
                    ? new AnonymousOpenAIClient(options)
                    : new OpenAIClient(new ApiKeyCredential(connection.ApiKey), options);
                IChatClient client = route.Protocol == ModelProtocol.Responses
                    ? openai.GetResponsesClient().AsIChatClient(connection.Model)
                    : openai.GetChatClient(connection.Model).AsIChatClient();
                if (route.Protocol == ModelProtocol.ChatCompletions)
                    client = new ChatReasoningClient(client, route.ReasoningField ?? "reasoning_content");
                return new OwnedChatClient(client, http);
            }
            catch
            {
                http.Dispose();
                throw;
            }
        }

        internal static Uri Address(string value)
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
            private readonly string m_MaxTokensField;
            public ApplicationHeaders(string conversation, string maxTokensField, HttpMessageHandler inner) : base(inner)
            {
                m_Conversation = conversation;
                m_MaxTokensField = maxTokensField;
            }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.Remove("User-Agent");
                request.Headers.UserAgent.ParseAdd(Application);
                if (m_Conversation != null)
                {
                    request.Headers.Remove("x-opencode-session");
                    request.Headers.Add("x-opencode-session", m_Conversation);
                }
                if (m_MaxTokensField == "max_tokens" && request.Content != null)
                {
                    JsonObject body = JsonNode.Parse(await request.Content.ReadAsStringAsync().ConfigureAwait(false)).AsObject();
                    if (body.TryGetPropertyValue("max_completion_tokens", out JsonNode tokens))
                    {
                        body.Remove("max_completion_tokens");
                        body["max_tokens"] = tokens;
                        request.Content.Dispose();
                        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    }
                }
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class ChatReasoningClient : DelegatingChatClient
        {
            private readonly byte[] m_Field;
            public ChatReasoningClient(IChatClient inner, string field) : base(inner) => m_Field = Encoding.UTF8.GetBytes("$." + field);
            public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default) =>
                InnerClient.GetResponseAsync(WithReasoning(messages), options, cancellationToken);
            public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default) =>
                InnerClient.GetStreamingResponseAsync(WithReasoning(messages), options, cancellationToken);

            private IEnumerable<ChatMessage> WithReasoning(IEnumerable<ChatMessage> messages)
            {
                foreach (ChatMessage message in messages)
                {
                    TextReasoningContent[] reasoning = message.Contents.OfType<TextReasoningContent>().ToArray();
                    if (message.Role != ChatRole.Assistant || reasoning.Length == 0)
                    {
                        yield return message;
                        continue;
                    }
                    var raw = new OpenAI.Chat.AssistantChatMessage(message.Text);
                    foreach (FunctionCallContent call in message.Contents.OfType<FunctionCallContent>())
                        raw.ToolCalls.Add(OpenAI.Chat.ChatToolCall.CreateFunctionToolCall(call.CallId, call.Name,
                            new BinaryData(JsonSerializer.SerializeToUtf8Bytes(call.Arguments))));
                    raw.Patch.Set(m_Field, string.Concat(reasoning.Select(content => content.Text)));
                    yield return new ChatMessage(message.Role, message.Contents) { RawRepresentation = raw };
                }
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
