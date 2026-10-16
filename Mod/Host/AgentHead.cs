using System;
using System.IO;
using AgentRuntime;

namespace airimayor.Host
{
    /// <summary>
    /// Opens the chat session. The city and the settings check both use this.
    /// </summary>
    internal static class AgentHead
    {
        internal static IAgentSession Open()
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
                    Record(),
                    message => Mod.log.Warn(message));
            }
            return new AgentRuntime.AgentRuntime(
                tools,
                AgentSystemPrompt.Text,
                ReadModel,
                Record(),
                message => Mod.log.Warn(message));
        }

        private static SessionRecordOptions Record()
        {
            return new SessionRecordOptions
            {
                Directory = ModPaths.LogsDirectory,
                Clock = () => CS2MCP.BridgeSystem.Instance?.Clock,
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
