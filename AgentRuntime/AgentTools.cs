using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRuntime
{
    /// <summary>
    /// Game tools the runtime can list and call. set_plan is not in this list.
    /// </summary>
    public interface IAgentTools
    {
        IReadOnlyList<AgentToolDeclaration> List(bool visionAvailable);

        Task<AgentToolResult> InvokeAsync(
            string name,
            string argumentsJson,
            CancellationToken cancellationToken);
    }

    public sealed class AgentToolDeclaration
    {
        public string Name;
        public string Description;
        public string ParametersJson;
    }

    public sealed class AgentToolResult
    {
        public bool Success;
        public string Text;
        public byte[] ImagePng;
        public string ImageCaption;
        public byte[] PreviewJpeg;

        /// <summary>Clock where the simulation thread ran the request. Null when the tool never reached it.</summary>
        public SimulationStamp Applied;

        /// <summary>Real time the request waited for the simulation thread. Null when it never queued.</summary>
        public long? QueuedMs;

        /// <summary>Diagnostic files kept with the call in the session record. Never shown to the model.</summary>
        public IReadOnlyDictionary<string, byte[]> Files;

        /// <summary>City ledger before and after a write. Session record only; never shown to the model.</summary>
        public JsonObject Evidence;
    }

    /// <summary>Timeline anchors of one tool call.</summary>
    public struct ToolAnchors
    {
        public SimulationStamp Submitted;
        public SimulationStamp Applied;
        public SimulationStamp Completed;
        public long? QueuedMs;
    }

    public enum ModelWire
    {
        ChatCompletions,
        Responses,
    }

    public sealed class ModelSettings
    {
        public string Endpoint;
        public string ApiKey;
        public string Model;
        public long WindowTokens;
        public bool Vision;
        public bool ContinueWhenIdle;
        public ModelWire Wire;
    }
}
