using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace airimayor.Agent
{
    public sealed class ModelRoute
    {
        public ModelProtocol Protocol { get; }
        public string Endpoint { get; }
        public string ReasoningField { get; }
        public string MaxTokensField { get; }

        internal ModelRoute(ModelProtocol protocol, string endpoint, string reasoningField = null, string maxTokensField = null)
        {
            Protocol = protocol;
            Endpoint = endpoint;
            ReasoningField = reasoningField;
            MaxTokensField = maxTokensField;
        }
    }

    public sealed class ProviderModel
    {
        public string Id { get; }
        public string Name { get; }
        public int? ContextTokens { get; }
        public int? OutputTokens { get; }
        public bool? Tools { get; }
        public bool? Images { get; }
        public IReadOnlyList<ModelRoute> Routes { get; }

        internal ProviderModel(string id, string name, int? contextTokens, bool? tools, bool? images,
            IEnumerable<ModelRoute> routes = null, int? outputTokens = null)
        {
            Id = id;
            Name = name;
            ContextTokens = contextTokens;
            OutputTokens = outputTokens;
            Tools = tools;
            Images = images;
            Routes = Array.AsReadOnly((routes ?? Array.Empty<ModelRoute>()).ToArray());
        }
    }

    public sealed class ModelProvider
    {
        public string Id { get; }
        public string Name { get; }
        public IReadOnlyList<string> Credentials { get; }
        public IReadOnlyList<ProviderModel> Models { get; }

        internal ModelProvider(string id, string name, IEnumerable<string> credentials, IEnumerable<ProviderModel> models)
        {
            Id = id;
            Name = name;
            Credentials = Array.AsReadOnly(credentials.ToArray());
            Models = Array.AsReadOnly(models.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public sealed class ResolvedModelConnection
    {
        public ProviderConnection Settings { get; }
        public ModelRoute Route { get; }
        public int WindowTokens { get; }
        public int OutputTokens { get; }
        public bool Images { get; }
        public bool RequiresKey { get; }

        internal ResolvedModelConnection(ProviderConnection settings, ModelRoute route, int windowTokens,
            int outputTokens, bool images, bool requiresKey)
        {
            Settings = settings;
            Route = route;
            WindowTokens = windowTokens;
            OutputTokens = outputTokens;
            Images = images;
            RequiresKey = requiresKey;
        }
    }

    /// <summary>An immutable catalog. A mayor selection captures this snapshot until the next Apply.</summary>
    public sealed class ModelCatalogSnapshot
    {
        public static ModelCatalogSnapshot Empty { get; } = new ModelCatalogSnapshot(Array.Empty<ModelProvider>());
        public IReadOnlyList<ModelProvider> Providers { get; }

        private ModelCatalogSnapshot(IEnumerable<ModelProvider> providers) =>
            Providers = Array.AsReadOnly(providers.OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase).ToArray());

        public ModelProvider Find(string id) => Providers.FirstOrDefault(provider => provider.Id == id);

        public ResolvedModelConnection Resolve(ProviderConnection settings)
        {
            if (!Enum.IsDefined(typeof(ModelProtocol), settings.Protocol))
                throw new InvalidOperationException("Choose a supported model protocol.");
            if (string.IsNullOrWhiteSpace(settings.Model))
                throw new InvalidOperationException("Choose or enter a model, then apply settings.");
            bool custom = settings.ProviderId == ModelProviders.Compatible;
            ModelProvider provider = custom ? null : Find(settings.ProviderId);
            ProviderModel model = provider?.Models.FirstOrDefault(item => item.Id == settings.Model);
            if (!custom && model == null && (settings.Protocol == ModelProtocol.Automatic || string.IsNullOrWhiteSpace(settings.Endpoint)))
                throw new InvalidOperationException("This model is not in the catalog. Refresh it, or enter its server address and protocol in advanced settings.");
            ModelRoute route = model?.Routes.FirstOrDefault(item => settings.Protocol == ModelProtocol.Automatic || item.Protocol == settings.Protocol);
            if (route == null)
            {
                if (settings.Protocol == ModelProtocol.Automatic || string.IsNullOrWhiteSpace(settings.Endpoint))
                    throw new InvalidOperationException("Choose a protocol supported by this model.");
                route = new ModelRoute(settings.Protocol, settings.Endpoint);
            }
            else if (!string.IsNullOrWhiteSpace(settings.Endpoint))
            {
                route = new ModelRoute(route.Protocol, settings.Endpoint, route.ReasoningField, route.MaxTokensField);
            }
            ModelProviders.Address(route.Endpoint);
            if (!string.IsNullOrEmpty(settings.ApiKey) && !string.IsNullOrEmpty(settings.CredentialOrigin) &&
                settings.CredentialOrigin != Origin(route.Endpoint))
                throw new InvalidOperationException("This connection now uses another host. Re-enter its API key before applying settings.");
            int window = settings.WindowTokens == 0 ? model?.ContextTokens ?? 128_000 : settings.WindowTokens;
            if (window < 32_000 || (model?.ContextTokens != null && window > model.ContextTokens.Value))
                throw new InvalidOperationException("Choose a context budget within this model's window, or use 0 for automatic.");
            int output = Math.Min(16_384, model?.OutputTokens ?? 16_384);
            return new ResolvedModelConnection(settings, route, window, output, model?.Images ?? true,
                !custom && (provider == null || provider.Credentials.Count > 0));
        }

        public static ModelCatalogSnapshot Parse(string json)
        {
            if (!(JsonNode.Parse(json) is JsonObject providers) || providers.Count == 0)
                throw new InvalidDataException("The model catalog is not a Models.dev v2 provider map.");
            var result = new List<ModelProvider>();
            foreach (KeyValuePair<string, JsonNode> entry in providers)
            {
                if (!(entry.Value is JsonObject provider) || provider["id"]?.GetValue<string>() != entry.Key ||
                    !(provider["models"] is JsonObject models) || !(provider["env"] is JsonArray credentials))
                    throw new InvalidDataException("The model catalog has an invalid provider.");
                var available = new List<ProviderModel>();
                foreach (KeyValuePair<string, JsonNode> item in models)
                {
                    JsonNode model = item.Value;
                    if (model?["type"]?.GetValue<string>() != "chat" ||
                        model["capabilities"]?["tools"]?["supported"]?.GetValue<bool>() != true ||
                        !HasModality(model["modalities"]?["input"], "text") || !HasModality(model["modalities"]?["output"], "text") ||
                        model["status"]?.GetValue<string>() == "deprecated")
                        continue;
                    int? context = model["limit"]?["context"]?.GetValue<int>();
                    int? output = model["limit"]?["output"]?.GetValue<int>();
                    if (context < 32_000 || output <= 0) continue;
                    if (!(model["api"] is JsonObject apis))
                        throw new InvalidDataException("The model catalog is missing v2 model routes.");
                    var routes = new List<ModelRoute>();
                    foreach (KeyValuePair<string, JsonNode> api in apis)
                    {
                        if (!TryProtocol(api.Key, out ModelProtocol protocol)) continue;
                        string address = api.Value["base_url"]?.GetValue<string>();
                        string path = api.Value["path"]?.GetValue<string>();
                        string expectedPath = protocol == ModelProtocol.Responses ? "/responses" :
                            protocol == ModelProtocol.ChatCompletions ? "/chat/completions" : "/messages";
                        if (!ValidAddress(address) || (path != null && path != expectedPath)) continue;
                        JsonObject compatibility = api.Value["compatibility"] as JsonObject;
                        if (compatibility != null && compatibility.Any(pair => pair.Key != "reasoning_field" && pair.Key != "max_tokens_field")) continue;
                        string reasoning = compatibility?["reasoning_field"]?.GetValue<string>();
                        string tokens = compatibility?["max_tokens_field"]?.GetValue<string>();
                        if ((reasoning != null && (protocol != ModelProtocol.ChatCompletions || reasoning != "reasoning_content")) ||
                            (tokens != null && (protocol != ModelProtocol.ChatCompletions || (tokens != "max_tokens" && tokens != "max_completion_tokens"))))
                            continue;
                        routes.Add(new ModelRoute(protocol, address, reasoning, tokens));
                    }
                    if (routes.Count == 0) continue;
                    if (model["id"]?.GetValue<string>() != item.Key)
                        throw new InvalidDataException("The model catalog has an invalid model ID.");
                    available.Add(new ProviderModel(item.Key, model["name"]?.GetValue<string>() ?? item.Key, context, true,
                        HasModality(model["modalities"]?["input"], "image"), routes.OrderBy(route =>
                            route.Protocol == ModelProtocol.Responses ? 0 : route.Protocol == ModelProtocol.AnthropicMessages ? 1 : 2), output));
                }
                if (available.Count > 0 && entry.Key != ModelProviders.Compatible)
                    result.Add(new ModelProvider(entry.Key, provider["name"]?.GetValue<string>() ?? entry.Key,
                        credentials.Select(value => value.GetValue<string>()), available));
            }
            if (result.Count == 0)
                throw new InvalidDataException("The catalog contains no supported chat models with city-tool support.");
            return new ModelCatalogSnapshot(result);
        }

        internal static string Origin(string endpoint) => ModelProviders.Address(endpoint).GetLeftPart(UriPartial.Authority);
        private static bool HasModality(JsonNode node, string type) => node is JsonArray values &&
            values.Any(value => value.GetValue<string>() == type || value.GetValue<string>().StartsWith(type + "/", StringComparison.Ordinal));
        private static bool ValidAddress(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri address) &&
            (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp) &&
            string.IsNullOrEmpty(address.UserInfo) && string.IsNullOrEmpty(address.Query) && string.IsNullOrEmpty(address.Fragment) &&
            value.IndexOf('$') < 0 && value.IndexOf('{') < 0;

        private static bool TryProtocol(string value, out ModelProtocol protocol)
        {
            protocol = value == "responses" ? ModelProtocol.Responses : value == "chat-completions" ?
                ModelProtocol.ChatCompletions : value == "messages" ? ModelProtocol.AnthropicMessages : ModelProtocol.Automatic;
            return protocol != ModelProtocol.Automatic;
        }
    }

    /// <summary>Loads and atomically replaces the public catalog; failed refreshes retain the last valid snapshot.</summary>
    public sealed class ModelCatalog
    {
        public const string Endpoint = "https://models.dev/experimental/v2.0/api.json";
        private static readonly HttpClient s_Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string m_Path;
        private readonly HttpClient m_Http;
        private readonly SemaphoreSlim m_Gate = new SemaphoreSlim(1, 1);
        private ModelCatalogSnapshot m_Current = ModelCatalogSnapshot.Empty;
        private string m_ETag;
        public ModelCatalogSnapshot Current => Volatile.Read(ref m_Current);

        public ModelCatalog(string cachePath, HttpClient http = null)
        {
            m_Path = cachePath;
            m_Http = http ?? s_Http;
        }

        public void Load()
        {
            if (!File.Exists(m_Path)) return;
            JsonObject root = JsonNode.Parse(File.ReadAllText(m_Path)) as JsonObject;
            if (root == null || root.Count != 3 || root["schema"]?.GetValue<int>() != 1 ||
                !(root["catalog"] is JsonObject data) || root["etag"] == null)
                throw new InvalidDataException("Unsupported model catalog cache format.");
            ModelCatalogSnapshot candidate = ModelCatalogSnapshot.Parse(data.ToJsonString());
            m_ETag = root["etag"].GetValue<string>();
            Volatile.Write(ref m_Current, candidate);
        }

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            await m_Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, Endpoint))
                {
                    request.Headers.UserAgent.ParseAdd(ModelProviders.Application);
                    if (!string.IsNullOrEmpty(m_ETag)) request.Headers.TryAddWithoutValidation("If-None-Match", m_ETag);
                    using (HttpResponseMessage response = await m_Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.NotModified && Current.Providers.Count > 0) return;
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException("Model catalog returned HTTP " + (int)response.StatusCode + ".");
                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        ModelCatalogSnapshot candidate = ModelCatalogSnapshot.Parse(json);
                        string etag = response.Headers.ETag?.ToString() ?? "";
                        string saved = new JsonObject { ["schema"] = 1, ["etag"] = etag, ["catalog"] = JsonNode.Parse(json) }.ToJsonString();
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(m_Path)));
                        string pending = m_Path + ".tmp";
                        File.WriteAllText(pending, saved);
                        if (File.Exists(m_Path)) File.Replace(pending, m_Path, null);
                        else File.Move(pending, m_Path);
                        m_ETag = etag;
                        Volatile.Write(ref m_Current, candidate);
                    }
                }
            }
            finally { m_Gate.Release(); }
        }
    }
}
