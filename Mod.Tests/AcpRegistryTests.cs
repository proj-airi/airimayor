using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using airimayor.Agent;
using airimayor.Host;
using Xunit;

namespace airimayor.Tests
{
    public sealed class AcpRegistryTests : IDisposable
    {
        private readonly string m_Root = Path.Combine(Path.GetTempPath(), "airimayor-acp-" + Guid.NewGuid().ToString("N"));
        private readonly RegistryHttp m_Handler = new RegistryHttp();
        private readonly HttpClient m_Http;
        private readonly AcpRegistry m_Registry;

        public AcpRegistryTests()
        {
            m_Http = new HttpClient(m_Handler);
            m_Registry = new AcpRegistry(m_Root, m_Http);
        }

        [Fact]
        public async Task Cached_catalog_survives_a_failed_refresh_without_changing_selection()
        {
            m_Handler.Index = Index(
                Agent("zulu", new JsonObject { ["npx"] = new JsonObject { ["package"] = "zulu@1.0.0" } }),
                Agent("alpha", new JsonObject { ["uvx"] = new JsonObject { ["package"] = "alpha==1.0.0" } }),
                Agent("unix", new JsonObject { ["binary"] = new JsonObject { ["linux-x86_64"] = new JsonObject() } }));
            await m_Registry.RefreshAsync();
            Assert.Equal(new[] { "alpha", "zulu" }, m_Registry.Agents.Select(agent => agent.Id));
            m_Handler.Fail = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => m_Registry.RefreshAsync());
            var cached = new AcpRegistry(m_Root, m_Http);
            cached.Load();
            Assert.Equal(new[] { "alpha", "zulu" }, cached.Agents.Select(agent => agent.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => cached.PrepareAsync("missing", m_Root));
        }

        [Fact]
        public void Unknown_cached_format_is_rejected()
        {
            Directory.CreateDirectory(m_Root);
            File.WriteAllText(Path.Combine(m_Root, "registry.json"), "{\"version\":\"2.0.0\",\"agents\":[]}");
            Assert.Throws<InvalidDataException>(() => m_Registry.Load());
            Assert.Empty(m_Registry.Agents);
        }

        [Fact]
        public async Task Binary_install_preserves_arguments_and_environment_and_is_reused()
        {
            m_Handler.Archive = Zip("bin/mayor.cmd", "@echo off\r\necho %~1\r\necho %~2\r\necho %CITY_TEST%\r\n");
            JsonObject binary = Binary("./bin/mayor.cmd");
            binary["args"] = new JsonArray("a value with spaces", "trailing\\");
            binary["env"] = new JsonObject { ["CITY_TEST"] = "city tools" };
            m_Handler.Index = Index(Agent("sample", new JsonObject { ["binary"] = new JsonObject { ["windows-x86_64"] = binary } }));
            await m_Registry.RefreshAsync();
            ProcessStartInfo start = await m_Registry.PrepareAsync("sample", Path.Combine(m_Root, "city work"));
            using (Process process = Process.Start(start))
            {
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
                Assert.Empty(error);
                Assert.Equal(new[] { "a value with spaces", "trailing\\", "city tools" },
                    output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            }
            await m_Registry.PrepareAsync("sample", m_Root);
            Assert.Equal(1, m_Handler.Downloads);
            Assert.Empty(Directory.GetDirectories(m_Root, ".install-*"));
        }

        [Fact]
        public async Task Failed_checksum_cannot_be_reused_as_an_installation()
        {
            m_Handler.Archive = Zip("mayor.exe", "sample");
            JsonObject binary = Binary("mayor.exe");
            binary["sha256"] = new string('0', 64);
            m_Handler.Index = Index(Agent("sample", new JsonObject { ["binary"] = new JsonObject { ["windows-x86_64"] = binary } }));
            await m_Registry.RefreshAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => m_Registry.PrepareAsync("sample", m_Root));
            Assert.Empty(Directory.GetDirectories(m_Root, ".install-*"));
            binary["sha256"] = Convert.ToHexString(SHA256.HashData(m_Handler.Archive));
            m_Handler.Index = Index(Agent("sample", new JsonObject { ["binary"] = new JsonObject { ["windows-x86_64"] = binary.DeepClone() } }));
            await m_Registry.RefreshAsync();
            ProcessStartInfo start = await m_Registry.PrepareAsync("sample", m_Root);
            Assert.Equal("sample", File.ReadAllText(start.FileName));
            Assert.Equal(2, m_Handler.Downloads);
        }

        [Fact]
        public async Task Raw_executable_download_is_prepared_without_an_archive_tool()
        {
            m_Handler.Archive = Encoding.UTF8.GetBytes("raw executable");
            JsonObject binary = Binary("./mayor.exe");
            binary["archive"] = "https://download.test/mayor.exe";
            m_Handler.Index = Index(Agent("sample", new JsonObject { ["binary"] = new JsonObject { ["windows-x86_64"] = binary } }));
            await m_Registry.RefreshAsync();
            ProcessStartInfo start = await m_Registry.PrepareAsync("sample", m_Root);
            Assert.Equal(m_Handler.Archive, File.ReadAllBytes(start.FileName));
        }

        [Fact]
        public async Task Launch_command_cannot_escape_the_installation_directory()
        {
            m_Handler.Archive = Zip("mayor.exe", "sample");
            JsonObject binary = Binary("../../outside.exe");
            m_Handler.Index = Index(Agent("sample", new JsonObject { ["binary"] = new JsonObject { ["windows-x86_64"] = binary } }));
            await m_Registry.RefreshAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => m_Registry.PrepareAsync("sample", m_Root));
            Assert.Equal(0, m_Handler.Downloads);
        }

        [Fact]
        public async Task Cancellation_cleans_up_an_unfinished_download()
        {
            m_Handler.Archive = Zip("mayor.exe", "sample");
            m_Handler.Index = Index(Agent("sample", new JsonObject { ["binary"] = new JsonObject { ["windows-x86_64"] = Binary("mayor.exe") } }));
            await m_Registry.RefreshAsync();
            m_Handler.HoldDownload = true;
            using var cancellation = new CancellationTokenSource();
            Task<ProcessStartInfo> preparation = m_Registry.PrepareAsync("sample", m_Root, cancellation.Token);
            await m_Handler.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            Assert.Empty(Directory.GetDirectories(m_Root, ".install-*"));
        }

        [Fact]
        public async Task Closing_the_session_stops_package_runner_descendants()
        {
            string marker = Path.Combine(m_Root, "processes.json");
            m_Handler.Archive = Zip(
                ("mayor.cmd", "@echo off\r\nnode \"%~dp0agent.js\"\r\n"),
                ("agent.js", """
                const fs = require('fs');
                const child = require('child_process').spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {stdio:'ignore'});
                require('readline').createInterface({input:process.stdin}).on('line', line => {
                  const request = JSON.parse(line);
                  let result;
                  if (request.method === 'initialize') result = {protocolVersion:1,agentCapabilities:{},authMethods:[]};
                  if (request.method === 'session/new') {
                    result = {sessionId:'root'};
                    fs.writeFileSync(process.env.CITY_MARKER, JSON.stringify([process.pid, child.pid]));
                  }
                  if (result) process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:request.id,result}) + '\n');
                });
                """));
            JsonObject binary = Binary("mayor.cmd");
            binary["env"] = new JsonObject { ["CITY_MARKER"] = marker };
            m_Handler.Index = Index(Agent("sample", new JsonObject { ["binary"] = new JsonObject { ["windows-x86_64"] = binary } }));
            await m_Registry.RefreshAsync();
            var session = new AcpSession(new EmptyTools(), "City work.", () => false,
                m_Registry, "sample", typeof(AcpRegistryTests).Assembly.Location, m_Root, null, null);
            var children = new List<Process>();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(marker))
                    await Task.Delay(10, timeout.Token);
                foreach (JsonNode pid in JsonNode.Parse(await File.ReadAllTextAsync(marker)).AsArray())
                    children.Add(Process.GetProcessById(pid.GetValue<int>()));
                session.Dispose();
                foreach (Process child in children)
                    await child.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                session.Dispose();
                foreach (Process child in children)
                {
                    if (!child.HasExited) child.Kill(true);
                    child.Dispose();
                }
            }
        }

        private JsonObject Binary(string command) => new JsonObject
        {
            ["archive"] = "https://download.test/mayor.zip",
            ["cmd"] = command,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(m_Handler.Archive)),
        };

        private static JsonObject Agent(string id, JsonObject distribution) => new JsonObject
        {
            ["id"] = id,
            ["name"] = id,
            ["version"] = "1.0.0",
            ["distribution"] = distribution,
        };

        private static string Index(params JsonObject[] agents) => new JsonObject
        {
            ["version"] = "1.0.0",
            ["agents"] = new JsonArray(agents.Cast<JsonNode>().ToArray()),
        }.ToJsonString();

        private static byte[] Zip(string path, string text) => Zip((path, text));

        private static byte[] Zip(params (string Path, string Text)[] files)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                foreach (var file in files)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(file.Path).Open(), new UTF8Encoding(false));
                    writer.Write(file.Text);
                }
            }
            return stream.ToArray();
        }

        private sealed class EmptyTools : IAgentTools
        {
            public IReadOnlyList<AgentToolDeclaration> List(bool visionAvailable) => Array.Empty<AgentToolDeclaration>();
            public Task<AgentToolResult> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("This test does not invoke city tools.");
        }

        public void Dispose()
        {
            m_Http.Dispose();
            string path = Path.GetFullPath(m_Root);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test directory escaped the temporary root.");
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private sealed class RegistryHttp : HttpMessageHandler
        {
            public string Index;
            public byte[] Archive;
            public bool Fail;
            public bool HoldDownload;
            public int Downloads;
            public TaskCompletionSource DownloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (Fail)
                    throw new HttpRequestException("Registry unavailable.");
                if (request.RequestUri.AbsoluteUri == AcpRegistry.IndexUrl)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Index) };
                Downloads++;
                DownloadStarted.TrySetResult();
                if (HoldDownload)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) };
            }
        }
    }
}
