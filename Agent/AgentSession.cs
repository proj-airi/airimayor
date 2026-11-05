using System;

namespace airimayor.Agent
{
    /// <summary>
    /// The chat's session: prompt, cancel, and the update stream.
    /// Implemented by the built-in and external mayor sessions.
    /// </summary>
    public interface IAgentSession : IDisposable
    {
        event Action<SessionUpdate> Updated;

        /// <summary>This session's record. City code writes its notes here while the session is live.</summary>
        SessionRecord Record { get; }

        void Prompt(string text);

        void Prompt(string modelText, string displayText, string placesJson);

        void Cancel();

        string ChatStateJson();
    }
}
