using System;
using System.IO;
using System.Text.Json.Nodes;
using airimayor.Agent;

namespace airimayor.Host
{
    internal sealed class MayorSelection
    {
        public readonly string Source;
        public readonly string ExternalAgent;
        public readonly ProviderConnection Connection;

        public MayorSelection(string source, string externalAgent, ProviderConnection connection)
        {
            if (source != AgentHead.BuiltIn && source != AgentHead.External)
                throw new InvalidOperationException("Unknown mayor source.");
            Source = source;
            ExternalAgent = externalAgent;
            Connection = connection;
        }
    }

    /// <summary>
    /// Opens the chat session. The city and the settings check both use this.
    /// </summary>
    internal static class AgentHead
    {
        internal const string BuiltIn = "built-in";
        internal const string External = "external";
        internal static readonly AcpRegistry Registry = new AcpRegistry(Path.Combine(ModPaths.RuntimeDataDirectory, "acp"));

        /// <param name="city">Loaded city name; null for the settings check.</param>
        internal static IAgentSession Open(string city, MayorSelection selection = null)
        {
            selection = selection ?? Setting.AppliedMayor;
            var tools = new Cs2AgentTools();
            if (selection.Source == External)
            {
                if (string.IsNullOrEmpty(selection.ExternalAgent))
                    throw new InvalidOperationException("Choose an external agent, then apply settings.");
                string root = Mod.InstallDirectory;
                string mcp = string.IsNullOrEmpty(root)
                    ? null
                    : Path.Combine(root, "mcp", "airimayor-mcp.exe");
                return new AcpSession(
                    tools,
                    AgentSystemPrompt.Text,
                    () => Setting.StaticVisionTools,
                    Registry,
                    selection.ExternalAgent,
                    mcp,
                    Path.Combine(ModPaths.RuntimeDataDirectory, "city"),
                    Record(selection, city),
                    message => Mod.log.Warn(message));
            }
            ProviderConnection connection = selection.Connection;
            return new ClientSession(id => ModelProviders.CreateChatClient(connection, id), tools, AgentSystemPrompt.Text,
                connection.WindowTokens, 16_384, Setting.StaticVisionTools,
                Record(selection, city), message => Mod.log.Warn(message));
        }

        private static SessionRecordOptions Record(MayorSelection selection, string city)
        {
            return new SessionRecordOptions
            {
                Root = ModPaths.SessionsDirectory,
                Clock = () => CS2MCP.BridgeSystem.Instance?.Clock,
                Facts = new JsonObject
                {
                    ["head"] = selection.Source == BuiltIn ? BuiltIn : selection.ExternalAgent,
                    ["modVersion"] = typeof(Mod).Assembly.GetName().Version.ToString(),
                    ["city"] = city,
                    ["provider"] = selection.Source == BuiltIn ? selection.Connection.ProviderId : null,
                    ["model"] = selection.Source == BuiltIn ? selection.Connection.Model : null,
                    ["protocol"] = selection.Source == BuiltIn ? selection.Connection.Protocol.ToString() : "ACP",
                },
            };
        }
    }
}
