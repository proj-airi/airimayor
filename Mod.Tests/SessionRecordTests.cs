using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using airimayor.Agent;
using Xunit;

namespace airimayor.Tests
{
    public class SessionRecordTests : IDisposable
    {
        private readonly string m_Root = Path.Combine(Path.GetTempPath(), "airimayor-record-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(m_Root)) Directory.Delete(m_Root, true);
        }

        private SessionRecord Open(string id = "record", Action<string> warn = null) => new SessionRecord(id, new SessionRecordOptions { Root = m_Root }, warn);
        private static JsonObject[] Lines(SessionRecord record)
        {
            using var stream = new FileStream(Path.Combine(record.Directory, SessionRecord.RecordsFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => (JsonObject)JsonNode.Parse(line)).ToArray();
        }
        private static JsonObject Data(SessionRecord record, JsonObject line) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(record.Directory, (string)line["attrs"]["airimayor.data"]["$asset"])));

        [Fact]
        public void A_recording_announces_its_source_and_definitions_and_stops_last()
        {
            var record = Open();
            record.TurnStart("t1", "build a road");
            record.TurnFinish(new JsonObject { ["stopReason"] = "cancelled" });
            record.System("host", "between turns");
            record.DeclareGap("external model calls are unavailable");
            record.DeclareGap("external model calls are unavailable");
            record.Dispose();
            record.Dispose();

            JsonObject[] lines = Lines(record);
            Assert.Equal("source.started", (string)lines[0]["type"]);
            Assert.Equal(1, (int)lines[0]["format"]);
            Assert.Equal("source.stopped", (string)lines[lines.Length - 1]["type"]);
            Assert.Contains(lines, line => (string)line["type"] == "definition" && (string)line["key"] == "agents/{agent}/llm");
            Assert.Contains(lines, line => (string)line["type"] == "clock.snapshot");
            Assert.All(lines, line =>
            {
                Assert.Equal("cs2:game", (string)line["source"]);
                Assert.Equal("record", (string)line["epoch"]);
                Assert.Equal("record", (string)line["ids"]["conversation"]);
                Assert.Equal("mono:cs2:game:record", (string)line["clock"]);
                Assert.True((double)line["ts"] >= 0);
            });
            Assert.Equal(Enumerable.Range(0, lines.Length).Select(i => (long)i), lines.Select(line => (long)line["seq"]));
            var begin = Assert.Single(lines, line => (string)line["name"] == "chat-turn");
            var end = Assert.Single(lines, line => (string)line["type"] == "span.end");
            Assert.Equal((string)begin["spanId"], (string)end["spanId"]);
            Assert.Equal("cancelled", (string)end["status"]);
            Assert.Equal("t1", (string)end["ids"]["turn"]);
            Assert.Null(Assert.Single(lines, line => (string)line["type"] == "log")["ids"]["turn"]);
            Assert.Equal("external model calls are unavailable", (string)Data(record, Assert.Single(lines, line => (string)line["name"] == "recording.gap"))["message"]);
            Assert.False(File.Exists(Path.Combine(record.Directory, "session.json")));
            Assert.False(File.Exists(Path.Combine(record.Directory, "timeline.jsonl")));
        }

        [Fact]
        public void Assets_keep_full_redacted_content_and_deduplicate_after_redaction()
        {
            using var record = Open();
            string full = new string('x', 70000);
            var original = new JsonObject { ["text"] = full, ["apiKey"] = "sk-private123456", ["authorization"] = "Bearer secret" };
            JsonObject first = record.KeepAsset(original);
            JsonObject second = record.KeepAsset(new JsonObject { ["text"] = full, ["apiKey"] = "different", ["authorization"] = "other" });
            Assert.Equal((string)first["$asset"], (string)second["$asset"]);
            JsonObject serialized = record.KeepAsset(Encoding.UTF8.GetBytes(original.ToJsonString()), "application/json");
            Assert.True(JsonNode.DeepEquals(first, serialized));
            Assert.Equal("sk-private123456", (string)original["apiKey"]);
            byte[] bytes = File.ReadAllBytes(Path.Combine(record.Directory, (string)first["$asset"]));
            Assert.Equal(bytes.Length, (int)first["bytes"]);
            var restored = JsonNode.Parse(bytes);
            Assert.Equal(full, (string)restored["text"]);
            Assert.Equal("[REDACTED]", (string)restored["apiKey"]);
            Assert.DoesNotContain("secret", Encoding.UTF8.GetString(bytes));

            JsonObject text = record.KeepAsset(Encoding.UTF8.GetBytes("token=private Bearer private sk-private123456 pk-private123456 rk-private123456"), "text/plain");
            Assert.DoesNotContain("private", File.ReadAllText(Path.Combine(record.Directory, (string)text["$asset"])));
            byte[] image = { 1, 2, 3, 255 };
            JsonObject png = record.KeepAsset(image, "image/png");
            Assert.Equal(image, File.ReadAllBytes(Path.Combine(record.Directory, (string)png["$asset"])));
        }

        [Fact]
        public void City_tool_evidence_keeps_full_results_anchors_and_returned_files()
        {
            using var record = Open();
            record.TurnStart("turn", "build");
            using var run = record.BindRun("root", "root-run", "mayor");
            var clock = new SimulationStamp(1200, "2026-01-02 08:00", 0f);
            string resultText = new string('r', 70000);
            record.FunctionStart("call/1", "build_network");
            record.Function("call/1", "build_network", "{\"apiKey\":\"private\"}", new AgentToolResult
            {
                Success = true, Text = resultText, ImagePng = new byte[] { 1, 2, 3 },
                Files = new Dictionary<string, byte[]> { ["probe.txt"] = Encoding.UTF8.GetBytes("token=private") },
                Evidence = new JsonObject { ["before"] = new JsonObject { ["networkEdges"] = 20 }, ["after"] = new JsonObject { ["networkEdges"] = 21 } },
            }, 40, new ToolAnchors { Submitted = clock, Applied = new SimulationStamp(1201, clock.GameTime, 0f), Completed = clock, QueuedMs = 16 });

            JsonObject[] lines = Lines(record);
            JsonObject evidence = Assert.Single(lines, line => (string)line["name"] == "city.tool.result");
            JsonObject data = Data(record, evidence);
            Assert.Equal(resultText, (string)data["result"]);
            Assert.Equal(21, (int)data["evidence"]["after"]["networkEdges"]);
            Assert.Equal(1201L, (long)data["timeline"]["applied"]["frame"]);
            Assert.Equal(16, (int)data["queuedMs"]);
            Assert.DoesNotContain("private", (string)data["arguments"]);
            JsonArray files = (JsonArray)data["files"];
            Assert.Equal(new[] { "image.png", "probe.txt" }, files.Select(file => (string)file["name"]));
            Assert.All(files, file => Assert.True(File.Exists(Path.Combine(record.Directory, (string)file["asset"]["$asset"]))));
            Assert.Equal("token=[REDACTED]", File.ReadAllText(Path.Combine(record.Directory, (string)files[1]["asset"]["$asset"])));
            JsonObject begin = Assert.Single(lines, line => (string)line["name"] == "execute_tool");
            Assert.Equal((string)begin["spanId"], (string)evidence["spanId"]);
            Assert.Contains(lines, line => (string)line["type"] == "span.end" && (string)line["spanId"] == (string)begin["spanId"]);
            Assert.All(lines.Where(line => (string)line["ids"]?["toolCall"] == "call/1"), line =>
            {
                Assert.Equal("turn", (string)line["ids"]["turn"]);
                Assert.Equal("root-run", (string)line["ids"]["run"]);
            });
            record.TurnFinish(null);
        }

        [Fact]
        public void Requested_tool_cancellation_is_recorded_separately_from_failure()
        {
            using var record = Open();
            record.FunctionStart("cancelled", "map_text");
            record.Function("cancelled", "map_text", "{}", new AgentToolResult { Success = false, Text = "interrupted" },
                10, default, cancelled: true);
            var lines = Lines(record);
            Assert.Equal("cancelled", (string)Assert.Single(lines, line => (string)line["type"] == "span.end")["status"]);
            Assert.Equal("cancelled", (string)Data(record, Assert.Single(lines, line => (string)line["name"] == "city.tool.result"))["status"]);
        }

        [Fact]
        public void An_unapplied_tool_has_no_invented_simulation_anchor()
        {
            using var record = Open();
            record.FunctionStart("call", "map_text");
            record.Function("call", "map_text", "{}", new AgentToolResult { Success = true, Text = "{}" }, 0, new ToolAnchors());
            JsonObject data = Data(record, Assert.Single(Lines(record), line => (string)line["name"] == "city.tool.result"));
            Assert.Empty((JsonObject)data["timeline"]);
            Assert.Null(data["queuedMs"]);
        }

        [Fact]
        public void A_native_tool_receives_evidence_without_a_second_span()
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "airimayor.harness",
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(listener);
            using var source = new ActivitySource("airimayor.harness");
            using var activity = source.StartActivity("execute_tool build_network");
            using var record = Open();
            record.FunctionStart("native-call", "build_network");
            record.Function("native-call", "build_network", "{}", new AgentToolResult { Success = true, Text = "{}" }, 0, new ToolAnchors());
            JsonObject[] lines = Lines(record);
            Assert.DoesNotContain(lines, line => (string)line["type"] == "span.begin" || (string)line["type"] == "span.end");
            Assert.Equal(activity.SpanId.ToHexString(), (string)Assert.Single(lines, line => (string)line["name"] == "city.tool.result")["spanId"]);
        }

        [Fact]
        public async Task Background_work_retains_its_origin_after_the_root_starts_another_turn()
        {
            using var record = Open();
            record.TurnStart("first", "inspect");
            using var root = record.BindRun("root", "root-run", "mayor");
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task child = Task.Run(async () =>
            {
                using var run = record.BindRun("child", "child-run", "inspector");
                ready.SetResult(true);
                await release.Task;
                JsonObject ids = record.TraceIds;
                ids["originTurn"] = "mutated-copy";
                record.System("child", "finished");
            });
            await ready.Task;
            record.TurnFinish(null);
            record.TurnStart("second", "continue");
            release.SetResult(true);
            await child;
            JsonObject log = Assert.Single(Lines(record), line => (string)line["type"] == "log");
            Assert.Null(log["ids"]["turn"]);
            Assert.Equal("first", (string)log["ids"]["originTurn"]);
            Assert.Equal("root-run", (string)log["ids"]["originRun"]);
            Assert.Equal("child-run", (string)log["ids"]["run"]);
            Assert.Equal("inspector", (string)log["ids"]["agent"]);
            record.TurnFinish(null);
        }

        [Fact]
        public void Captured_child_ids_do_not_acquire_a_later_root_turn()
        {
            using var record = Open();
            record.TurnStart("first", "inspect");
            using var root = record.BindRun("root", "root-run", "mayor");
            JsonObject captured;
            using (record.BindRun("child", "child-run", "inspector")) captured = record.TraceIds;
            record.TurnFinish(null);
            record.TurnStart("second", "continue");
            captured["conversation"] = "other";
            record.EmitTrace(new JsonObject
            {
                ["type"] = "instant", ["name"] = "child.finished", ["track"] = "agents/inspector/control", ["ids"] = captured,
            });
            using (record.BindContext(captured))
            {
                record.System("child", "late tool");
                record.FunctionStart("late-child-call", "map_text");
                record.Function("late-child-call", "map_text", "{}", new AgentToolResult { Success = true, Text = "{}" }, 0, new ToolAnchors());
            }

            Assert.All(Lines(record).Where(line => (string)line["ids"]["agentSession"] == "child"), line =>
            {
                Assert.Null(line["ids"]["turn"]);
                Assert.Equal("first", (string)line["ids"]["originTurn"]);
                Assert.Equal("root-run", (string)line["ids"]["originRun"]);
                Assert.Equal("child-run", (string)line["ids"]["run"]);
                Assert.Equal("inspector", (string)line["ids"]["agent"]);
                Assert.Equal(record.SessionId, (string)line["ids"]["conversation"]);
            });
            Assert.Equal("other", (string)captured["conversation"]);
            Assert.Equal("second", (string)record.TraceIds["turn"]);
            record.TurnFinish(null);
        }

        [Fact]
        public void Invalid_attributes_are_rejected_and_io_failure_leaves_an_incomplete_record()
        {
            var warnings = new List<string>();
            var record = Open(warn: warnings.Add);
            Assert.Throws<ArgumentException>(() => record.EmitTrace(new JsonObject
            {
                ["type"] = "instant", ["track"] = "city/events", ["name"] = "invalid", ["attrs"] = new JsonObject { ["nested"] = new JsonObject() },
            }));
            File.WriteAllText(Path.Combine(record.Directory, "assets"), "block the asset directory");
            Assert.Null(record.KeepAsset(Encoding.UTF8.GetBytes("evidence"), "text/plain"));
            record.Dispose();
            Assert.Contains(warnings, warning => warning.Contains("incomplete"));
            Assert.DoesNotContain(Lines(record), line => (string)line["type"] == "source.stopped");
        }
    }
}
