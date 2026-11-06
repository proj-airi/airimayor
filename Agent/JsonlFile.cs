using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace airimayor.Agent
{
    internal sealed class JsonlFile : IDisposable
    {
        private static readonly Regex s_SecretPattern = new Regex(
            "(?i)(\\\"?(?:api[_ -]?key|authorization|x-api-key|token)\\\"?\\s*[:=]\\s*)(\\\"[^\\\"]*\\\"|[^\\\"\\s,}]+)", RegexOptions.Compiled);
        private static readonly Regex s_BearerPattern = new Regex("(?i)(bearer\\s+)([^\\s,}\\\"]+)", RegexOptions.Compiled);
        private static readonly Regex s_KeyPattern = new Regex("\\b(?:sk|pk|rk)-[A-Za-z0-9_-]{8,}", RegexOptions.Compiled);
        private readonly StreamWriter m_Writer;

        public JsonlFile(string path) => m_Writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));

        public void Append(JsonObject line)
        {
            m_Writer.WriteLine(line.ToJsonString());
            m_Writer.Flush();
        }

        public static string RedactSecrets(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            return s_SecretPattern.Replace(s_BearerPattern.Replace(s_KeyPattern.Replace(value, "[REDACTED]"), "$1[REDACTED]"), match =>
                match.Groups[1].Value + (match.Groups[2].Value.StartsWith("\"", StringComparison.Ordinal) ? "\"[REDACTED]\"" : "[REDACTED]"));
        }

        public void Dispose() => m_Writer.Dispose();
    }
}
