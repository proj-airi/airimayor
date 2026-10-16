using System;

namespace AgentRuntime
{
    /// <summary>
    /// The chat's session: prompt, cancel, and the update stream.
    /// The built-in runtime and an external ACP head both sit here.
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
