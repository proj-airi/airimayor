using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AgentRuntime;
using Xunit;

namespace airimayor.Tests
{
    public class AgentObservabilityTests
    {
        [Fact]
        public void Every_line_carries_the_envelope_and_a_turn_closes()
        {
            string directory = Path.Combine(Path.GetTempPath(), "airimayor-record-" + Guid.NewGuid().ToString("N"));
            var clock = new SimulationStamp(1200, "2026-01-02 08:00", 0f);
            try
            {
                using (var record = new AgentObservability(
                    "s1", new SessionRecordOptions { Directory = directory, Clock = () => clock }, null))
                {
                    record.TurnStart("t1", "build a road");
                    record.FunctionStart("call-1", "build_network");
                    record.Function("call-1", "build_network", "{}", "{\"placed\":true}", true, 40,
                        new ToolAnchors
                        {
                            Submitted = clock,
                            Applied = new SimulationStamp(1201, "2026-01-02 08:00", 0f),
                            Completed = clock,
                            QueuedMs = 16,
                        });
                    record.TurnFinish(1, 1, 50, null, null);
                    record.System("bridge", "between turns");
                }

                JsonObject[] lines = File.ReadAllLines(Path.Combine(directory, "agent-timeline-s1.jsonl"))
                    .Select(line => (JsonObject)JsonNode.Parse(line))
                    .ToArray();

                Assert.All(lines, line => Assert.Equal(AgentObservability.Schema, (int)line["schema"]));
                Assert.All(lines, line => Assert.Equal(1200L, (long)line["clock"]["frame"]));
                Assert.Equal("t1", (string)lines[1]["turn"]);
                Assert.Equal("function.start", (string)lines[1]["type"]);
                Assert.Equal("call-1", (string)lines[1]["call"]);

                JsonObject function = lines[2];
                Assert.Equal("call-1", (string)function["call"]);
                Assert.Equal(16L, (long)function["data"]["queuedMs"]);
                Assert.Equal(1201L, (long)function["data"]["timeline"]["applied"]["frame"]);

                Assert.Equal("t1", (string)lines[3]["turn"]);
                Assert.Null(lines[4]["turn"]);
                Assert.Null(lines[4]["call"]);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [Fact]
        public void A_tool_that_never_reached_the_simulation_has_no_applied_anchor()
        {
            string directory = Path.Combine(Path.GetTempPath(), "airimayor-record-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var record = new AgentObservability("s2", new SessionRecordOptions { Directory = directory }, null))
                {
                    record.Function("call-2", "set_plan", "{}", "{}", true, 0, new ToolAnchors());
                }

                var line = (JsonObject)JsonNode.Parse(File.ReadAllLines(Path.Combine(directory, "agent-timeline-s2.jsonl"))[0]);
                Assert.Null(line["clock"]);
                Assert.Null(line["data"]["queuedMs"]);
                Assert.Empty((JsonObject)line["data"]["timeline"]);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
    }
}
