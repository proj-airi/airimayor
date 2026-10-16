using System;
using System.IO;

namespace airimayor.Host
{
    /// <summary>Filesystem locations for agent state and logs.</summary>
    /// <remarks>Runtime files stay outside the watched mod asset directory.</remarks>
    public static class ModPaths
    {
        public const string ModId = "airimayor";

        /// <summary>
        /// User data root: CSII_USERDATAPATH when set (dev builds), otherwise
        /// the game's LocalLow profile. Never contains API keys.
        /// </summary>
        public static string UserDataRoot
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("CSII_USERDATAPATH");
                if (!string.IsNullOrEmpty(env))
                {
                    return env;
                }
                // LocalLow is a sibling of Local, not Local\Low.
                string localLow = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "LocalLow",
                    "Colossal Order",
                    "Cities Skylines II");
                return localLow;
            }
        }

        public static string RuntimeDataDirectory =>
            Path.Combine(UserDataRoot, "ModsData", ModId);

        /// <summary>One self-contained record directory per chat session (ADR 0046).</summary>
        public static string SessionsDirectory => Path.Combine(RuntimeDataDirectory, "sessions");

        /// <summary>
        /// Development payloads live outside Mods so rebuilding them does not
        /// trigger the game's asset watcher or a Gameface media reload.
        /// </summary>
        public static string HotReloadDirectory => Path.Combine(RuntimeDataDirectory, "hot-reload");

        public static string HotReloadHandlersFile =>
            Path.Combine(HotReloadDirectory, "RequestHandlers.payload");

        public static string HotReloadToolCatalogFile =>
            Path.Combine(HotReloadDirectory, "ToolCatalog.json");
    }
}
