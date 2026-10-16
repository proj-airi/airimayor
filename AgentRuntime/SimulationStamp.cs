using System.Text.Json.Nodes;

namespace AgentRuntime
{
    /// <summary>
    /// Simulation clock reading taken on the simulation thread: frame,
    /// in-game time, and selected speed (0 = paused). Immutable so the
    /// host can publish it across threads.
    /// </summary>
    public sealed class SimulationStamp
    {
        public SimulationStamp(long frame, string gameTime, float speed)
        {
            Frame = frame;
            GameTime = gameTime;
            Speed = speed;
        }

        public long Frame { get; }

        public string GameTime { get; }

        public float Speed { get; }

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["frame"] = Frame,
                ["gameTime"] = GameTime,
                ["speed"] = Speed,
            };
        }
    }
}
