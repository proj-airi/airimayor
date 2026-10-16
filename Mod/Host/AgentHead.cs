using System;
using System.IO;
using System.Text.Json.Nodes;
using AgentRuntime;

namespace airimayor.Host
{
    /// <summary>
    /// Opens the chat session. The city and the settings check both use this.
    /// </summary>
    internal static class AgentHead
    {
        /// <param name="city">Loaded city name; null for the settings check.</param>
        internal static IAgentSession Open(string city)
        {
            var tools = new Cs2AgentTools();
            AgentChoice choice = AgentChoices.Find(Setting.StaticHead);
            if (!choice.IsBuiltIn)
            {
                string root = Mod.InstallDirectory;
                string mcp = string.IsNullOrEmpty(root)
                    ? null
                    : Path.Combine(root, "mcp", "airimayor-mcp.exe");
                return new AcpSession(
                    tools,
                    AgentSystemPrompt.Text,
                    () => Setting.StaticVisionTools,
                    () => Setting.StaticContinuous,
                    Setting.StaticStartupPrompt,
                    choice.Command,
                    choice.Arguments,
                    mcp,
                    string.IsNullOrEmpty(root) ? Directory.GetCurrentDirectory() : root,
                    Record(choice, city),
                    message => Mod.log.Warn(message));
            }
            return new AgentRuntime.AgentRuntime(
                tools,
                AgentSystemPrompt.Text,
                ReadModel,
                Record(choice, city),
                message => Mod.log.Warn(message));
        }

        private static SessionRecordOptions Record(AgentChoice choice, string city)
        {
            return new SessionRecordOptions
            {
                Root = ModPaths.SessionsDirectory,
                Clock = () => CS2MCP.BridgeSystem.Instance?.Clock,
                Facts = new JsonObject
                {
                    ["head"] = choice.IsBuiltIn ? AgentChoices.BuiltIn : choice.Id,
                    ["modVersion"] = typeof(Mod).Assembly.GetName().Version.ToString(),
                    ["city"] = city,
                },
            };
        }

        private static ModelSettings ReadModel()
        {
            return new ModelSettings
            {
                Endpoint = Setting.StaticEndpoint,
                ApiKey = Setting.StaticApiKey,
                Model = Setting.StaticModel,
                WindowTokens = Setting.StaticWindowTokens,
                Vision = Setting.StaticVisionTools,
                ContinueWhenIdle = Setting.StaticContinuous,
                Wire = Setting.StaticApiKind == ApiKind.Responses
                    ? ModelWire.Responses
                    : ModelWire.ChatCompletions,
            };
        }
    }
}
