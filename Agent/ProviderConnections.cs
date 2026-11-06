using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace airimayor.Agent
{
    public enum ModelProtocol
    {
        Automatic,
        Responses,
        ChatCompletions,
        AnthropicMessages,
    }

    public sealed class ProviderConnection
    {
        public string ProviderId { get; }
        public string Endpoint { get; }
        public string ModelsEndpoint { get; }
        public string ApiKey { get; }
        public string Model { get; }
        public ModelProtocol Protocol { get; }
        public int WindowTokens { get; }
        public string CredentialOrigin { get; }

        public ProviderConnection(string providerId, string endpoint, string modelsEndpoint, string apiKey,
            string model, ModelProtocol protocol, int windowTokens, string credentialOrigin = "")
        {
            ProviderId = providerId;
            Endpoint = endpoint ?? "";
            ModelsEndpoint = modelsEndpoint ?? "";
            ApiKey = apiKey ?? "";
            Model = model ?? "";
            Protocol = protocol;
            WindowTokens = windowTokens;
            CredentialOrigin = credentialOrigin ?? "";
        }

        public ProviderConnection With(string endpoint = null, string modelsEndpoint = null, string apiKey = null,
            string model = null, ModelProtocol? protocol = null, int? windowTokens = null, string credentialOrigin = null)
        {
            return new ProviderConnection(ProviderId, endpoint ?? Endpoint, modelsEndpoint ?? ModelsEndpoint,
                apiKey ?? ApiKey, model ?? Model, protocol ?? Protocol, windowTokens ?? WindowTokens,
                credentialOrigin ?? CredentialOrigin);
        }

        internal bool SameCatalog(ProviderConnection other) => other != null &&
            ProviderId == other.ProviderId && Endpoint == other.Endpoint && ModelsEndpoint == other.ModelsEndpoint &&
            ApiKey == other.ApiKey && Protocol == other.Protocol;
    }

    /// <summary>Saved provider connections and catalogs matched to the connection that requested them.</summary>
    public sealed class ProviderConnections
    {
        private readonly object m_Gate = new object();
        private readonly Dictionary<string, ProviderConnection> m_Connections = new Dictionary<string, ProviderConnection>();
        private readonly Dictionary<string, Catalog> m_Catalogs = new Dictionary<string, Catalog>();

        public ProviderConnection Read(string providerId)
        {
            lock (m_Gate)
            {
                return m_Connections.TryGetValue(providerId, out ProviderConnection saved)
                    ? saved : new ProviderConnection(providerId, "", "", "", "",
                        providerId == ModelProviders.Compatible ? ModelProtocol.ChatCompletions : ModelProtocol.Automatic, 0);
            }
        }

        public void Set(ProviderConnection connection)
        {
            if (string.IsNullOrWhiteSpace(connection.ProviderId) || !Enum.IsDefined(typeof(ModelProtocol), connection.Protocol) ||
                (connection.WindowTokens != 0 && connection.WindowTokens < 32_000))
                throw new InvalidDataException("Invalid model connection settings.");
            lock (m_Gate)
            {
                ProviderConnection previous = Read(connection.ProviderId);
                if (Origin(previous.Endpoint) != Origin(connection.Endpoint) && previous.ApiKey == connection.ApiKey)
                    connection = connection.With(apiKey: "", credentialOrigin: "");
                m_Connections[connection.ProviderId] = connection;
            }
        }

        public IReadOnlyList<ProviderModel> Models(string providerId)
        {
            lock (m_Gate)
            {
                return m_Catalogs.TryGetValue(providerId, out Catalog catalog) && catalog.Source.SameCatalog(Read(providerId))
                    ? catalog.Models : Array.Empty<ProviderModel>();
            }
        }

        public bool AcceptModels(ProviderConnection source, IReadOnlyList<ProviderModel> models)
        {
            lock (m_Gate)
            {
                if (!source.SameCatalog(Read(source.ProviderId)))
                    return false;
                m_Catalogs[source.ProviderId] = new Catalog { Source = source, Models = models.ToArray() };
                return true;
            }
        }

        public string ToJson()
        {
            lock (m_Gate)
            {
                var entries = new JsonArray();
                foreach (ProviderConnection connection in m_Connections.Values.OrderBy(item => item.ProviderId, StringComparer.Ordinal))
                {
                    entries.Add(new JsonObject
                    {
                        ["provider"] = connection.ProviderId,
                        ["endpoint"] = connection.Endpoint,
                        ["modelsEndpoint"] = connection.ModelsEndpoint,
                        ["apiKey"] = connection.ApiKey,
                        ["model"] = connection.Model,
                        ["protocol"] = connection.Protocol.ToString(),
                        ["windowTokens"] = connection.WindowTokens,
                        ["credentialOrigin"] = connection.CredentialOrigin,
                    });
                }
                return new JsonObject { ["schema"] = 2, ["connections"] = entries }.ToJsonString();
            }
        }

        public static ProviderConnections FromJson(string json)
        {
            JsonObject root = JsonNode.Parse(json)?.AsObject();
            RequireKeys(root, "schema", "connections");
            if (root["schema"]?.GetValue<int>() != 2 || !(root["connections"] is JsonArray entries))
                throw new InvalidDataException("Unsupported model connection settings format.");
            var profiles = new ProviderConnections();
            foreach (JsonNode node in entries)
            {
                JsonObject entry = node.AsObject();
                RequireKeys(entry, "provider", "endpoint", "modelsEndpoint", "apiKey", "model", "protocol", "windowTokens", "credentialOrigin");
                string provider = entry["provider"].GetValue<string>();
                string protocolName = entry["protocol"].GetValue<string>();
                if (!Enum.TryParse(protocolName, out ModelProtocol protocol) || protocol.ToString() != protocolName ||
                    !Enum.IsDefined(typeof(ModelProtocol), protocol) || string.IsNullOrWhiteSpace(provider) || profiles.m_Connections.ContainsKey(provider))
                    throw new InvalidDataException("Invalid model connection settings.");
                int window = entry["windowTokens"].GetValue<int>();
                if (window != 0 && window < 32_000)
                    throw new InvalidDataException("Invalid context budget.");
                profiles.m_Connections.Add(provider, new ProviderConnection(provider,
                    entry["endpoint"].GetValue<string>(), entry["modelsEndpoint"].GetValue<string>(),
                    entry["apiKey"].GetValue<string>(), entry["model"].GetValue<string>(), protocol, window,
                    entry["credentialOrigin"].GetValue<string>()));
            }
            return profiles;
        }

        private static void RequireKeys(JsonObject value, params string[] keys)
        {
            if (value == null || value.Count != keys.Length || keys.Any(key => value[key] == null))
                throw new InvalidDataException("Unsupported model connection settings format.");
        }

        private static string Origin(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri)
            ? uri.GetLeftPart(UriPartial.Authority) : endpoint;

        private sealed class Catalog
        {
            public ProviderConnection Source;
            public IReadOnlyList<ProviderModel> Models;
        }
    }
}
