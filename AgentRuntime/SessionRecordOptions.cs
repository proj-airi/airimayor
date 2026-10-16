using System;
using System.Text.Json.Nodes;

namespace AgentRuntime
{
    /// <summary>Where a session writes its record, how it reads the simulation clock, and the facts it starts with.</summary>
    public sealed class SessionRecordOptions
    {
        /// <summary>Directory that holds session records. Null writes nothing.</summary>
        public string Root;

        /// <summary>Latest simulation clock reading, or null while no city runs.</summary>
        public Func<SimulationStamp> Clock;

        /// <summary>Host facts that occur once, such as head, mod version, and city.</summary>
        public JsonObject Facts;

        /// <summary>Session, turn, and conversation that opened this one. Null for a chat the player opened.</summary>
        public JsonObject Parent;
    }
}
