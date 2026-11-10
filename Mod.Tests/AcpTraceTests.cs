using System.Text.Json.Nodes;
using airimayor.Agent;
using airimayor.Host;
using dotacp.protocol;
using Xunit;

namespace airimayor.Tests
{
    public sealed class AcpTraceTests
    {
        [Fact]
        public async Task Input_stop_and_protocol_updates_are_durable_without_a_model_completion()
        {
            string root = Path.Combine(Path.GetTempPath(), "airimayor-acp-trace-" + Guid.NewGuid().ToString("N"));
            using var session = new AcpSession(null, "City work.", () => false,
                new AcpRegistry(Path.GetTempPath()), "sample", null, Path.GetTempPath(),
                new SessionRecordOptions { Root = root }, null);
            // End initialization before a process is launched; exercise the host's public protocol seam.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (session.Status != AgentStatus.Error) await Task.Delay(10, timeout.Token);

            session.Prompt("inspect river", "Inspect this place", "[]");
            session.Cancel();
            int assetCount = Directory.GetFiles(Path.Combine(session.Record.Directory, "assets")).Length;
            await session.SessionUpdateAsync(new SessionNotification
            {
                SessionId = "external-session",
                Update = new SessionUpdateAgentThoughtChunk { Content = new TextContent { Text = "returned reasoning" } },
            });
            await session.SessionUpdateAsync(new SessionNotification
            {
                SessionId = "external-session",
                Update = new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "partial river report" } },
            });
            Assert.Equal(assetCount, Directory.GetFiles(Path.Combine(session.Record.Directory, "assets")).Length);
            await session.RequestPermissionAsync(new RequestPermissionRequest
            {
                SessionId = "external-session",
                Options = new[] { new PermissionOption { OptionId = "once", Kind = PermissionOptionKind.AllowOnce } },
            });

            JsonObject[] records;
            using (var reader = new StreamReader(new FileStream(Path.Combine(session.Record.Directory, SessionRecord.RecordsFile),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                records = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => (JsonObject)JsonNode.Parse(line)).ToArray();
            JsonObject Data(JsonObject record) => (JsonObject)JsonNode.Parse(File.ReadAllText(
                Path.Combine(session.Record.Directory, (string)record["attrs"]["airimayor.data"]["$asset"])));
            JsonObject received = Data(Assert.Single(records, record => (string)record["name"] == "input.received"));
            JsonObject queued = Data(Assert.Single(records, record => (string)record["name"] == "input.queued"));
            Assert.Equal((string)received["messageId"], (string)queued["messageId"]);
            Assert.Equal("inspect river", (string)received["text"]);
            Assert.Equal("Inspect this place", (string)received["displayText"]);
            Assert.DoesNotContain(records, record => (string)record["name"] == "input.applied");
            JsonObject requested = Data(Assert.Single(records, record => (string)record["name"] == "stop.requested"));
            JsonObject stopped = Data(Assert.Single(records, record => (string)record["name"] == "stop.applied"));
            Assert.Equal((string)requested["requestId"], (string)stopped["requestId"]);
            Assert.False((bool)stopped["active"]);
            JsonObject[] deltas = records.Where(record => (string)record["name"] == "acp.output.delta")
                .Select(record => JsonNode.Parse((string)record["attrs"]["airimayor.data"]).AsObject()).ToArray();
            Assert.Equal(new[] { 0, 1 }, deltas.Select(delta => (int)delta["index"]));
            Assert.All(deltas, delta => Assert.Equal("external-session", (string)delta["agentSession"]));
            Assert.Equal("returned reasoning", (string)deltas[0]["reasoning"]);
            Assert.Equal("partial river report", (string)deltas[1]["text"]);
            Assert.Contains("partial river report", deltas[1]["update"].ToJsonString());
            Assert.Contains("cancelled", Data(Assert.Single(records, record => (string)record["name"] == "acp.permission"))["response"].ToJsonString());
            session.Dispose();
            Directory.Delete(root, true);
        }
    }
}
