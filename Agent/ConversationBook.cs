using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace airimayor.Agent
{
    /// <summary>
    /// Conversations one root chat opened. <c>task</c> returns while the work
    /// is still running. The final text is delivered when the turn ends.
    /// Stop does not cancel these conversations. Disposing the session does.
    /// </summary>
    public sealed class ConversationBook
    {
        public const string TaskName = "task";

        private readonly object m_Lock = new object();
        private readonly Dictionary<string, Child> m_Children =
            new Dictionary<string, Child>(StringComparer.Ordinal);
        private readonly Func<string, string, CancellationToken, Task<string>> m_Run;
        private readonly Action<SessionUpdate> m_Emit;
        private readonly Action<string> m_Deliver;

        public ConversationBook(
            Func<string, string, CancellationToken, Task<string>> run,
            Action<SessionUpdate> emit,
            Action<string> deliver)
        {
            m_Run = run ?? throw new ArgumentNullException(nameof(run));
            m_Emit = emit ?? throw new ArgumentNullException(nameof(emit));
            m_Deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        }

        public IEnumerable<AgentToolDeclaration> List()
        {
            yield return new AgentToolDeclaration
            {
                Name = TaskName,
                Description = "Open a conversation and give it one task. The report comes back on this chat when that work finishes. Do not look it up.",
                ParametersJson = Required("description", "prompt"),
            };
        }

        public Task<AgentToolResult> InvokeAsync(
            string name,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(name, TaskName, StringComparison.Ordinal))
            {
                return Task.FromResult(Fail("Unknown conversation tool."));
            }
            JsonElement args = Parse(argumentsJson);
            return TaskAsync(Text(args, "description"), Text(args, "prompt"));
        }

        public void CancelAll()
        {
            List<Child> children;
            lock (m_Lock)
            {
                children = new List<Child>(m_Children.Values);
            }
            foreach (Child child in children)
            {
                Stop(child);
            }
        }

        private Task<AgentToolResult> TaskAsync(string description, string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return Task.FromResult(Fail("A conversation needs a task."));
            }
            var child = new Child
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Title = string.IsNullOrWhiteSpace(description) ? "Conversation" : description.Trim(),
                Status = "running",
                Stop = new CancellationTokenSource(),
            };
            lock (m_Lock)
            {
                m_Children[child.Id] = child;
            }
            _ = RunAsync(child, prompt);
            return Task.FromResult(Ok("That conversation is working. Its report will arrive on this chat."));
        }

        private async Task RunAsync(Child child, string prompt)
        {
            CancellationToken token = child.Stop.Token;
            try
            {
                string text = await m_Run(child.Id, prompt, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    Finish(child, "cancelled");
                    return;
                }
                Finish(child, "finished");
                Deliver(child, text ?? "");
            }
            catch (OperationCanceledException)
            {
                Finish(child, "cancelled");
            }
            catch (Exception e)
            {
                Finish(child, "error");
                Deliver(child, e.Message);
            }
        }

        private void Deliver(Child child, string text)
        {
            string body = string.IsNullOrWhiteSpace(text) ? "The conversation finished." : text;
            m_Emit(new SessionUpdate
            {
                Kind = "tool",
                CallId = "child:" + child.Id,
                Tool = child.Title,
                Result = body,
                Status = child.Status == "error" ? AgentStatus.Error : AgentStatus.Idle,
            });
            m_Deliver(child.Title + "\n" + body);
        }

        private void Stop(Child child)
        {
            try
            {
                child.Stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            Finish(child, "cancelled");
        }

        private void Finish(Child child, string status)
        {
            lock (m_Lock)
            {
                if (child.Status != "running" && status == "cancelled")
                {
                    return;
                }
                child.Status = status;
            }
        }

        private static AgentToolResult Ok(string text)
        {
            return new AgentToolResult { Success = true, Text = text ?? "" };
        }

        private static AgentToolResult Fail(string text)
        {
            return new AgentToolResult { Success = false, Text = text ?? "" };
        }

        private static JsonElement Parse(string argumentsJson)
        {
            string json = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                return document.RootElement.Clone();
            }
        }

        private static string Text(JsonElement args, string name)
        {
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
            return "";
        }

        private static string Required(params string[] names)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (string name in names)
            {
                properties[name] = new JsonObject { ["type"] = "string" };
                required.Add(name);
            }
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
            }.ToJsonString();
        }

        private sealed class Child
        {
            public string Id;
            public string Title;
            public string Status;
            public CancellationTokenSource Stop;
        }
    }
}
