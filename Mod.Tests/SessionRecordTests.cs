using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AgentRuntime;
using Xunit;

namespace airimayor.Tests
{
    public class SessionRecordTests : IDisposable
    {
        private readonly string m_Root = Path.Combine(Path.GetTempPath(), "airimayor-record-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(m_Root))
            {
                Directory.Delete(m_Root, true);
            }
        }

        [Fact]
        public void Every_line_carries_the_envelope_and_a_turn_closes()
        {
            var clock = new SimulationStamp(1200, "2026-01-02 08:00", 0f);
            string directory;
            using (var record = new SessionRecord("s1", new SessionRecordOptions { Root = m_Root, Clock = () => clock }, null))
            {
                directory = record.Directory;
                record.TurnStart("t1", "build a road");
                record.FunctionStart("call-1", "build_network");
                record.Function("call-1", "build_network", "{}",
                    new AgentToolResult { Success = true, Text = "{\"placed\":true}" }, 40,
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

            JsonObject[] lines = Lines(Path.Combine(directory, SessionRecord.TimelineFile));
            Assert.All(lines, line => Assert.Equal(SessionRecord.Schema, (int)line["schema"]));
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

        [Fact]
        public void A_tool_that_never_reached_the_simulation_has_no_applied_anchor()
        {
            string directory;
            using (var record = new SessionRecord("s2", new SessionRecordOptions { Root = m_Root }, null))
            {
                directory = record.Directory;
                record.Function("call-2", "set_plan", "{}", new AgentToolResult { Success = true, Text = "{}" }, 0, new ToolAnchors());
            }

            JsonObject line = Lines(Path.Combine(directory, SessionRecord.TimelineFile))[0];
            Assert.Null(line["clock"]);
            Assert.Null(line["data"]["queuedMs"]);
            Assert.Empty((JsonObject)line["data"]["timeline"]);
        }

        [Fact]
        public void The_session_file_gets_its_end_last_and_names_its_gaps()
        {
            var record = new SessionRecord("s3", new SessionRecordOptions
            {
                Root = m_Root,
                Facts = new JsonObject { ["head"] = "built-in", ["city"] = "Riverside" },
            }, null);
            string sessionFile = Path.Combine(record.Directory, SessionRecord.SessionFile);
            Assert.EndsWith("--s3", record.Directory);

            JsonObject open = (JsonObject)JsonNode.Parse(File.ReadAllText(sessionFile));
            Assert.Equal("Riverside", (string)open["city"]);
            Assert.Null(open["ended"]);

            record.DeclareGap("model calls are not observable");
            record.DeclareGap("model calls are not observable");
            record.Dispose();

            JsonObject closed = (JsonObject)JsonNode.Parse(File.ReadAllText(sessionFile));
            Assert.NotNull(closed["ended"]["utc"]);
            Assert.Single((JsonArray)closed["knownGaps"]);
        }

        [Fact]
        public void An_owned_conversation_records_inside_its_parent()
        {
            using (var parent = new SessionRecord("root", new SessionRecordOptions
            {
                Root = m_Root,
                Facts = new JsonObject { ["head"] = "built-in" },
            }, null))
            {
                parent.TurnStart("t9", "delegate");
                string childDirectory;
                using (var child = new SessionRecord("kid", parent.Conversation("c1"), null))
                {
                    childDirectory = child.Directory;
                }

                Assert.Equal(Path.Combine(parent.Directory, "conversations"), Path.GetDirectoryName(childDirectory));
                JsonObject facts = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(childDirectory, SessionRecord.SessionFile)));
                Assert.Equal("root", (string)facts["parent"]["session"]);
                Assert.Equal("t9", (string)facts["parent"]["turn"]);
                Assert.Equal("c1", (string)facts["parent"]["conversation"]);
                Assert.Equal("built-in", (string)facts["head"]);
            }
        }

        [Fact]
        public void Files_a_call_returned_are_kept_under_the_call()
        {
            string directory;
            using (var record = new SessionRecord("s4", new SessionRecordOptions { Root = m_Root }, null))
            {
                directory = record.Directory;
                record.Function("call/5", "map_image", "{}", new AgentToolResult
                {
                    Success = true,
                    Text = "{}",
                    ImagePng = new byte[] { 1, 2, 3 },
                    Files = new Dictionary<string, byte[]> { ["meta.json"] = new byte[] { 123, 125 } },
                }, 0, new ToolAnchors());
            }

            JsonObject line = Lines(Path.Combine(directory, SessionRecord.TimelineFile))[0];
            string[] files = ((JsonArray)line["data"]["files"]).Select(file => (string)file).ToArray();
            Assert.Equal(new[] { "calls/call_5/image.png", "calls/call_5/meta.json" }, files);
            Assert.True(File.Exists(Path.Combine(directory, "calls", "call_5", "image.png")));
        }

        private static JsonObject[] Lines(string path)
        {
            return File.ReadAllLines(path).Select(line => (JsonObject)JsonNode.Parse(line)).ToArray();
        }
    }
}
