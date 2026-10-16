using System;

namespace AgentRuntime
{
    /// <summary>Where a session writes its record and how it reads the simulation clock.</summary>
    public sealed class SessionRecordOptions
    {
        /// <summary>Record root. Null writes nothing.</summary>
        public string Directory;

        /// <summary>Latest simulation clock reading, or null while no city runs.</summary>
        public Func<SimulationStamp> Clock;
    }
}
