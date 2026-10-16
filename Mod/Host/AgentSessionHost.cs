using AgentRuntime;

namespace airimayor.Host
{
    /// <summary>
    /// The loaded-city runtime. The UI system creates and clears it.
    /// Mod unload disposes it if the UI system has not yet.
    /// </summary>
    internal static class AgentSessionHost
    {
        public static IAgentSession Current { get; set; }
    }

    /// <summary>
    /// Converged dev log: the agent timeline is the primary sink while a
    /// city session is live; the game log stays the fallback outside one
    /// and the error mirror inside one. Info stays timeline-only so the
    /// game log keeps boot, session bounds, and errors.
    /// </summary>
    public static class AgentTimeline
    {
        public static void Info(string source, string message)
        {
            SessionRecord record = AgentSessionHost.Current?.Record;
            if (record != null)
            {
                record.System(source, message);
                return;
            }
            Mod.log.Info(source + ": " + SessionRecord.RedactSecrets(message));
        }

        public static void Warn(string source, string message)
        {
            AgentSessionHost.Current?.Record?.Error(source, message);
            Mod.log.Warn(source + ": " + SessionRecord.RedactSecrets(message));
        }
    }
}
