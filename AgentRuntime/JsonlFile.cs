using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentRuntime
{
    /// <summary>
    /// Append-only JSONL file inside a session record: one line per write,
    /// flushed each line, secrets redacted, size-rotated. A rotation that
    /// drops the oldest file reports it, so the record can declare the gap.
    /// </summary>
    internal sealed class JsonlFile : IDisposable
    {
        public const long MaxFileBytes = 50L * 1024 * 1024;
        public const int KeepFiles = 5;

        private static readonly Regex s_JsonSecretPattern = new Regex(
            "(?i)(\\\"?(?:api[_ -]?key|authorization|x-api-key|token)\\\"?\\s*[:=]\\s*\\\")([^\\\"]*)(\\\")",
            RegexOptions.Compiled);
        private static readonly Regex s_SecretPattern = new Regex(
            "(?i)(\\\"?(?:api[_ -]?key|authorization|x-api-key|token)\\\"?\\s*[:=]\\s*)([^\\\"\\s,}]+)",
            RegexOptions.Compiled);
        private static readonly Regex s_BearerPattern = new Regex(
            "(?i)(bearer\\s+)([^\\s,}]+)",
            RegexOptions.Compiled);

        private readonly object m_Lock = new object();
        private readonly string m_Path;
        private readonly Action<string> m_Warn;
        private readonly Action<string> m_RotatedOut;
        private StreamWriter m_Writer;
        private long m_Size;

        public JsonlFile(string path, Action<string> warn, Action<string> rotatedOut)
        {
            m_Path = path;
            m_Warn = warn;
            m_RotatedOut = rotatedOut;
            m_Writer = new StreamWriter(path, true, new UTF8Encoding(false));
            m_Size = new FileInfo(path).Length;
        }

        public void Append(JsonObject line)
        {
            string json = RedactSecrets(line.ToJsonString());
            lock (m_Lock)
            {
                if (m_Writer == null)
                {
                    return;
                }
                try
                {
                    m_Writer.WriteLine(json);
                    m_Writer.Flush();
                    m_Size += Encoding.UTF8.GetByteCount(json) + 1;
                    if (m_Size > MaxFileBytes)
                    {
                        Rotate();
                    }
                }
                catch (Exception e)
                {
                    m_Warn?.Invoke("session record write failed: " + e.Message);
                }
            }
        }

        public static string RedactSecrets(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? "";
            }
            string redacted = s_JsonSecretPattern.Replace(value, "$1[REDACTED]$3");
            redacted = s_BearerPattern.Replace(redacted, "$1[REDACTED]");
            redacted = s_SecretPattern.Replace(redacted, "$1[REDACTED]");
            return redacted;
        }

        private void Rotate()
        {
            try
            {
                m_Writer.Dispose();
                m_Writer = null;
                string oldest = m_Path + "." + KeepFiles;
                if (File.Exists(oldest))
                {
                    File.Delete(oldest);
                    m_RotatedOut?.Invoke(Path.GetFileName(oldest));
                }
                for (int i = KeepFiles - 1; i >= 1; i--)
                {
                    string from = m_Path + "." + i;
                    if (File.Exists(from))
                    {
                        File.Move(from, m_Path + "." + (i + 1));
                    }
                }
                File.Move(m_Path, m_Path + ".1");
                m_Writer = new StreamWriter(m_Path, true, new UTF8Encoding(false));
                m_Size = 0;
            }
            catch (Exception e)
            {
                m_Warn?.Invoke("session record rotation failed: " + e.Message);
            }
        }

        public void Dispose()
        {
            lock (m_Lock)
            {
                m_Writer?.Dispose();
                m_Writer = null;
            }
        }
    }
}
