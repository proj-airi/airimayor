using System;
using System.ClientModel;
using System.IO;
using System.Text.Json.Nodes;
using airimayor.Agent;
using Microsoft.Extensions.AI;
using OpenAI;

namespace airimayor.Host
{
    /// <summary>
    /// Opens the chat session. The city and the settings check both use this.
    /// </summary>
    internal static class AgentHead
    {
        internal const string BuiltIn = "built-in";
        internal static readonly AcpRegistry Registry = new AcpRegistry(Path.Combine(ModPaths.RuntimeDataDirectory, "acp"));

        /// <param name="city">Loaded city name; null for the settings check.</param>
        internal static IAgentSession Open(string city)
        {
            var tools = new Cs2AgentTools();
            string head = Setting.StaticHead;
            if (head != BuiltIn)
            {
                string root = Mod.InstallDirectory;
                string mcp = string.IsNullOrEmpty(root)
                    ? null
                    : Path.Combine(root, "mcp", "airimayor-mcp.exe");
                return new AcpSession(
                    tools,
                    AgentSystemPrompt.Text,
                    () => Setting.StaticVisionTools,
                    Registry,
                    head,
                    mcp,
                    Path.Combine(ModPaths.RuntimeDataDirectory, "city"),
                    Record(head, city),
                    message => Mod.log.Warn(message));
            }
            string endpoint = Setting.StaticEndpoint;
            string apiKey = Setting.StaticApiKey;
            string model = Setting.StaticModel;
            return new ClientSession(() => OpenClient(endpoint, apiKey, model), tools, AgentSystemPrompt.Text,
                Setting.StaticWindowTokens, 16_384, Setting.StaticVisionTools,
                Record(head, city), message => Mod.log.Warn(message));
        }

        private static IChatClient OpenClient(string endpoint, string apiKey, string model)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("Set an API key and choose a model in settings, then apply.");
            return new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                NetworkTimeout = TimeSpan.FromMinutes(10),
            }).GetResponsesClient().AsIChatClient(model);
        }

        private static SessionRecordOptions Record(string head, string city)
        {
            return new SessionRecordOptions
            {
                Root = ModPaths.SessionsDirectory,
                Clock = () => CS2MCP.BridgeSystem.Instance?.Clock,
                Facts = new JsonObject
                {
                    ["head"] = head,
                    ["modVersion"] = typeof(Mod).Assembly.GetName().Version.ToString(),
                    ["city"] = city,
                    ["model"] = head == BuiltIn ? Setting.StaticModel : null,
                    ["protocol"] = head == BuiltIn ? "Responses" : "ACP",
                },
            };
        }
    }
}
