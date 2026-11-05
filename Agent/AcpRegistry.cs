using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace airimayor.Agent
{
    public sealed class AcpAgent
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string Version { get; internal set; }
        internal JsonObject Distribution;
    }

    /// <summary>Registry discovery and preparation of an agent's Windows launch command.</summary>
    public sealed class AcpRegistry
    {
        public const string IndexUrl = "https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json";
        private static readonly HttpClient s_Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private readonly string m_Directory;
        private readonly HttpClient m_Http;
        private readonly SemaphoreSlim m_Install = new SemaphoreSlim(1, 1);
        private AcpAgent[] m_Agents = Array.Empty<AcpAgent>();

        public AcpRegistry(string directory, HttpClient http = null)
        {
            m_Directory = Path.GetFullPath(directory);
            m_Http = http ?? s_Http;
        }

        public IReadOnlyList<AcpAgent> Agents => Volatile.Read(ref m_Agents);

        public void Load()
        {
            string file = Path.Combine(m_Directory, "registry.json");
            if (File.Exists(file))
                Volatile.Write(ref m_Agents, Parse(File.ReadAllText(file)));
        }

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using (HttpResponseMessage response = await m_Http.GetAsync(IndexUrl, timeout.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AcpAgent[] agents = Parse(json);
                    Directory.CreateDirectory(m_Directory);
                    string file = Path.Combine(m_Directory, "registry.json");
                    string temporary = file + "." + Guid.NewGuid().ToString("N");
                    File.WriteAllText(temporary, json);
                    if (File.Exists(file))
                        File.Replace(temporary, file, null);
                    else
                        File.Move(temporary, file);
                    Volatile.Write(ref m_Agents, agents);
                }
            }
        }

        public async Task<ProcessStartInfo> PrepareAsync(
            string id, string workingDirectory, CancellationToken cancellationToken = default)
        {
            if (Agents.Count == 0)
            {
                Load();
                if (Agents.Count == 0)
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            AcpAgent agent = Agents.FirstOrDefault(candidate => candidate.Id == id);
            if (agent == null)
                throw new InvalidOperationException("The selected agent is not in the ACP Registry. Refresh the agent list and select it again.");

            JsonObject distribution = agent.Distribution;
            var arguments = new List<string>();
            string command;
            if (distribution["binary"]?["windows-x86_64"] is JsonObject binary)
            {
                command = await InstallAsync(agent, binary, cancellationToken).ConfigureAwait(false);
                distribution = binary;
            }
            else if (distribution["npx"] is JsonObject npm)
            {
                // CreateProcess cannot execute npx.cmd; run npm's JS entry directly.
                string npx = FindCommand("npx.cmd");
                string script = Path.Combine(Path.GetDirectoryName(npx), "node_modules", "npm", "bin", "npx-cli.js");
                if (!File.Exists(script))
                    throw new FileNotFoundException("Install Node.js with npm to start this agent.", script);
                command = FindCommand("node.exe");
                arguments.Add(script);
                arguments.Add("--yes");
                arguments.Add(Required(npm, "package"));
                distribution = npm;
            }
            else if (distribution["uvx"] is JsonObject python)
            {
                command = FindCommand("uvx.exe");
                arguments.Add(Required(python, "package"));
                distribution = python;
            }
            else
            {
                throw new InvalidOperationException("This agent has no Windows distribution in the ACP Registry.");
            }
            if (distribution["args"] is JsonArray args)
                arguments.AddRange(args.Select(argument => argument.GetValue<string>()));

            Directory.CreateDirectory(workingDirectory);
            var start = new ProcessStartInfo
            {
                FileName = command,
                Arguments = string.Join(" ", arguments.Select(Quote)),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (distribution["env"] is JsonObject environment)
            {
                foreach (KeyValuePair<string, JsonNode> variable in environment)
                    start.EnvironmentVariables[variable.Key] = variable.Value.GetValue<string>();
            }
            if (Path.GetExtension(command).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(command).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                // Batch commands must not interpret registry values as shell syntax.
                if (new[] { command }.Concat(arguments).Any(value => value.IndexOfAny("\"%!?&|<>^\r\n".ToCharArray()) >= 0))
                    throw new InvalidDataException("The agent's batch command contains unsupported shell characters.");
                start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                start.Arguments = "/d /s /c \"" + string.Join(" ", new[] { command }.Concat(arguments)
                    .Select(value => "\"" + value + "\"")) + "\"";
            }
            return start;
        }

        private async Task<string> InstallAsync(AcpAgent agent, JsonObject binary, CancellationToken cancellationToken)
        {
            var uri = new Uri(Required(binary, "archive"), UriKind.Absolute);
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("The agent download must use HTTPS.");
            string fingerprint = Hash(Encoding.UTF8.GetBytes(binary.ToJsonString())).Substring(0, 16);
            string destination = ContainedPath(m_Directory, agent.Id + "/" + agent.Version + "-" + fingerprint);
            string command = ContainedPath(destination, Required(binary, "cmd"));
            await m_Install.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(command))
                    return command;
                string staging = ContainedPath(m_Directory, ".install-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    string archive = Path.Combine(staging, "download");
                    using (HttpResponseMessage response = await m_Http.GetAsync(
                        uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (Stream output = File.Create(archive))
                            await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                    }
                    string expected = binary["sha256"]?.GetValue<string>();
                    if (expected != null)
                    {
                        using (Stream input = File.OpenRead(archive))
                        using (SHA256 hash = SHA256.Create())
                        {
                            if (!string.Equals(Hex(hash.ComputeHash(input)), expected, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("The agent download failed its SHA-256 check.");
                        }
                    }
                    string unpacked = Path.Combine(staging, "unpacked");
                    Directory.CreateDirectory(unpacked);
                    string stagedCommand = ContainedPath(unpacked, Required(binary, "cmd"));
                    if (new[] { ".zip", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2" }.Any(
                        suffix => uri.AbsolutePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                        await ExtractAsync(archive, unpacked, cancellationToken).ConfigureAwait(false);
                    else if (uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(stagedCommand));
                        File.Move(archive, stagedCommand);
                    }
                    else
                        throw new InvalidDataException("The agent archive format is not supported on Windows.");
                    if (!File.Exists(stagedCommand))
                        throw new FileNotFoundException("The agent archive does not contain its launch command.", stagedCommand);
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    Directory.Move(unpacked, destination);
                    return command;
                }
                finally
                {
                    Directory.Delete(ContainedPath(m_Directory, Path.GetFileName(staging)), true);
                }
            }
            finally
            {
                m_Install.Release();
            }
        }

        private static async Task ExtractAsync(string archive, string destination, CancellationToken cancellationToken)
        {
            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FindCommand("tar.exe"),
                    Arguments = "-xf " + Quote(archive) + " -C " + Quote(destination),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            })
            {
                process.Start();
                Task<string> error = process.StandardError.ReadToEndAsync();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                using (cancellationToken.Register(() => { if (!process.HasExited) process.Kill(); }))
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                await output.ConfigureAwait(false);
                string detail = await error.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidDataException("Could not unpack the agent (exit " + process.ExitCode + "): " + detail.Trim());
            }
        }

        private static AcpAgent[] Parse(string json)
        {
            JsonNode root = JsonNode.Parse(json);
            if (root?["version"]?.GetValue<string>() != "1.0.0" || !(root["agents"] is JsonArray entries))
                throw new InvalidDataException("Unsupported ACP Registry format.");
            var agents = new List<AcpAgent>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonNode entry in entries)
            {
                string id = Required(entry, "id");
                string version = Required(entry, "version");
                if (!Regex.IsMatch(id, "^[a-z][a-z0-9-]*$") || !ids.Add(id) ||
                    !Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+$"))
                    throw new InvalidDataException("Invalid ACP Registry agent identity.");
                if (!(entry["distribution"] is JsonObject distribution))
                    throw new InvalidDataException("The ACP Registry agent has no distribution.");
                if (distribution["binary"]?["windows-x86_64"] == null && distribution["npx"] == null && distribution["uvx"] == null)
                    continue;
                agents.Add(new AcpAgent
                {
                    Id = id,
                    Name = Required(entry, "name"),
                    Version = version,
                    Distribution = distribution,
                });
            }
            if (agents.Count == 0)
                throw new InvalidDataException("The ACP Registry returned no Windows agents.");
            return agents.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string Required(JsonNode node, string name)
        {
            string value = node?[name]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("The ACP Registry is missing " + name + ".");
            return value;
        }

        private static string ContainedPath(string directory, string relative)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The agent path is outside its installation directory.");
            return path;
        }

        private static string FindCommand(string name)
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string path = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
            throw new FileNotFoundException("Command not found: " + name + ". Install it outside the game.");
        }

        private static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }
                quoted.Append('\\', slashes * (c == '"' ? 2 : 1));
                if (c == '"') quoted.Append('\\');
                quoted.Append(c);
                slashes = 0;
            }
            return quoted.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
                return Hex(hash.ComputeHash(bytes));
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
