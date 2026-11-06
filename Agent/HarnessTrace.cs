using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentSession = Microsoft.Agents.AI.AgentSession;

namespace airimayor.Agent
{
    // Native activities own spans; the leaf tap checkpoints requests before the model starts.
    internal sealed class HarnessTrace : IDisposable
    {
        internal const string SourceName = "airimayor.harness";
        private readonly SessionRecord m_Record;
        private readonly ActivityListener m_Listener;
        private readonly AsyncLocal<Run> m_Run = new AsyncLocal<Run>();
        private readonly ConditionalWeakTable<AgentSession, Identity> m_Sessions = new ConditionalWeakTable<AgentSession, Identity>();
        private readonly string m_Key = "airimayor.trace." + Guid.NewGuid().ToString("N");

        public HarnessTrace(SessionRecord record)
        {
            m_Record = record;
            m_Listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = Started,
                ActivityStopped = Stopped,
            };
            ActivitySource.AddActivityListener(m_Listener);
        }

        public IChatClient Observe(IChatClient client) => new CheckpointClient(client, this);

        public AIAgent Observe(AIAgent agent)
        {
            agent.GetService<OpenTelemetryAgent>().EnableSensitiveData = true;
            agent.GetService<IChatClient>().GetService<OpenTelemetryChatClient>().EnableSensitiveData = true;
            return agent.AsBuilder().Use(FunctionAsync).Use(RunAsync, RunStreamingAsync).Build();
        }

        private sealed class Identity
        {
            public readonly string Id = Guid.NewGuid().ToString("N");
        }

        private sealed class Run
        {
            public string Agent;
            public Activity Activity;
        }

        private sealed class Span
        {
            public string Track;
            public string Name;
            public JsonObject Ids;
            public Run Run;
        }

        private async ValueTask<object> FunctionAsync(AIAgent agent, FunctionInvocationContext context,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object>> next, CancellationToken cancellationToken)
        {
            Span span = FindSpan(Activity.Current);
            using (m_Record.BindContext(span.Ids))
                return await next(context, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AgentResponse> RunAsync(IEnumerable<ChatMessage> input, AgentSession session,
            AgentRunOptions options, AIAgent next, CancellationToken cancellationToken)
        {
            Run previous = m_Run.Value;
            m_Run.Value = new Run { Agent = next.Name };
            using (m_Record.BindRun(m_Sessions.GetOrCreateValue(session).Id, Guid.NewGuid().ToString("N"), next.Name))
            {
                try { return await next.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false); }
                finally { m_Run.Value = previous; }
            }
        }

        private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(IEnumerable<ChatMessage> input,
            AgentSession session, AgentRunOptions options, AIAgent next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Run previous = m_Run.Value;
            m_Run.Value = new Run { Agent = next.Name };
            using (m_Record.BindRun(m_Sessions.GetOrCreateValue(session).Id, Guid.NewGuid().ToString("N"), next.Name))
            {
                try
                {
                    await foreach (var update in next.RunStreamingAsync(input, session, options, cancellationToken).ConfigureAwait(false))
                        yield return update;
                }
                finally { m_Run.Value = previous; }
            }
        }

        private void Started(Activity activity)
        {
            Span parent = FindSpan(activity.Parent);
            Run run = m_Run.Value ?? parent?.Run;
            if (run == null) return;
            bool agent = run.Activity == null;
            if (agent) run.Activity = activity;
            bool tool = activity.OperationName.StartsWith("execute_tool ", StringComparison.Ordinal);
            var span = new Span
            {
                Track = "agents/" + run.Agent + "/" + (agent ? "turn" : tool ? "tools" : "llm"),
                Name = agent ? "invoke_agent " + run.Agent : activity.DisplayName,
                Ids = !agent && parent != null ? (JsonObject)parent.Ids.DeepClone() : m_Record.TraceIds,
                Run = run,
            };
            activity.SetCustomProperty(m_Key, span);
            JsonObject record = Event(activity, span, "span.begin");
            record["name"] = span.Name;
            if (activity.ParentSpanId != default)
            {
                if (agent && span.Ids["originRun"] != null)
                    record["links"] = new JsonArray(new JsonObject
                    {
                        ["rel"] = "caused-by",
                        ["to"] = new JsonObject { ["spanId"] = activity.ParentSpanId.ToHexString() },
                    });
                else
                    record["parentSpanId"] = activity.ParentSpanId.ToHexString();
            }
            record["attrs"] = Attributes(activity);
            m_Record.EmitTrace(record);
        }

        private Span FindSpan(Activity activity)
        {
            for (; activity != null; activity = activity.Parent)
                if (activity.GetCustomProperty(m_Key) is Span span) return span;
            return null;
        }

        private void Checkpoint(Activity activity)
        {
            if (activity?.GetCustomProperty(m_Key) is not Span span) return;
            JsonObject record = Event(activity, span, "span.update");
            record["attrs"] = Attributes(activity);
            m_Record.EmitTrace(record);
        }

        private void FirstChunk(Activity activity)
        {
            if (activity?.GetCustomProperty(m_Key) is not Span span) return;
            JsonObject record = Event(activity, span, "instant");
            record["name"] = "llm.first_chunk";
            m_Record.EmitTrace(record);
        }

        private void Stopped(Activity activity)
        {
            if (activity.GetCustomProperty(m_Key) is not Span span) return;
            JsonObject record = Event(activity, span, "span.end");
            record["attrs"] = Attributes(activity);
            string error = activity.GetTagItem("error.type") as string;
            record["status"] = error == typeof(OperationCanceledException).FullName || error == typeof(TaskCanceledException).FullName
                ? "cancelled" : activity.Status == ActivityStatusCode.Error ? "error" : "ok";
            if (activity.StatusDescription != null) record["message"] = activity.StatusDescription;
            if (error != null) record["reason"] = error;
            m_Record.EmitTrace(record);
        }

        private JsonObject Event(Activity activity, Span span, string type) => new JsonObject
        {
            ["type"] = type,
            ["ts"] = m_Record.TraceNow,
            ["track"] = span.Track,
            ["spanId"] = activity.SpanId.ToHexString(),
            ["ids"] = span.Ids.DeepClone(),
        };

        private JsonObject Attributes(Activity activity)
        {
            var attributes = new JsonObject();
            foreach (var tag in activity.TagObjects)
            {
                if (tag.Value == null || tag.Value is double number && (double.IsNaN(number) || double.IsInfinity(number)) ||
                    tag.Value is float single && (float.IsNaN(single) || float.IsInfinity(single))) continue;
                JsonNode value = tag.Value is string text && IsJson(tag.Key)
                    ? JsonNode.Parse(text) : JsonSerializer.SerializeToNode(tag.Value);
                Assets(value);
                if (value != null)
                    attributes[tag.Key] = value is JsonObject || value is JsonArray || value.ToJsonString().Length > 4096
                        ? m_Record.KeepAsset(value) : value;
            }
            return attributes;
        }

        private static bool IsJson(string key) => key == "gen_ai.input.messages" || key == "gen_ai.output.messages" ||
            key == "gen_ai.system_instructions" || key == "gen_ai.tool.definitions" ||
            key == "gen_ai.tool.call.arguments" || key == "gen_ai.tool.call.result" || key == "gen_ai.response.finish_reasons";

        private void Assets(JsonNode value)
        {
            if (value is JsonObject obj)
            {
                if (obj["type"] is JsonValue kind && kind.TryGetValue<string>(out var type) && type == "blob" &&
                    obj["content"] is JsonValue content && content.TryGetValue<string>(out var data))
                {
                    obj["content"] = m_Record.KeepAsset(Convert.FromBase64String(data), (string)obj["mime_type"]);
                }
                else
                {
                    foreach (var property in obj) Assets(property.Value);
                }
            }
            else if (value is JsonArray array)
            {
                foreach (var item in array) Assets(item);
            }
        }

        public void Dispose() => m_Listener.Dispose();

        private sealed class CheckpointClient : DelegatingChatClient
        {
            private readonly HarnessTrace m_Trace;
            public CheckpointClient(IChatClient inner, HarnessTrace trace) : base(inner) => m_Trace = trace;

            public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                m_Trace.Checkpoint(Activity.Current);
                return base.GetResponseAsync(messages, options, cancellationToken);
            }

            public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
                ChatOptions options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Activity activity = Activity.Current;
                m_Trace.Checkpoint(activity);
                bool first = true;
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    if (first)
                    {
                        first = false;
                        m_Trace.FirstChunk(activity);
                    }
                    yield return update;
                }
            }
        }
    }
}
