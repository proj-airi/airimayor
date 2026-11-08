using System;
using Microsoft.Extensions.Logging;

namespace airimayor.Agent
{
    // SDK diagnostics share the session trace; native activities carry model evidence.
    internal sealed class HostDiagnostics : ILoggerFactory
    {
        private readonly SessionRecord m_Record;
        private readonly Action<string> m_Warn;
        public HostDiagnostics(SessionRecord record, Action<string> warn = null)
        {
            m_Record = record;
            m_Warn = warn;
        }

        public ILogger CreateLogger(string category) => new Logger(m_Record, category, m_Warn);
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly SessionRecord m_Record;
            private readonly string m_Category;
            private readonly Action<string> m_Warn;
            public Logger(SessionRecord record, string category, Action<string> warn)
            {
                m_Record = record;
                m_Category = category;
                m_Warn = warn;
            }

            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (IsEnabled(level))
                {
                    string message = SessionRecord.RedactSecrets(formatter(state, exception));
                    m_Record.Log(m_Category, message, level >= LogLevel.Error ? "error" : "warn", exception);
                    m_Warn?.Invoke(message);
                }
            }
        }
    }
}
