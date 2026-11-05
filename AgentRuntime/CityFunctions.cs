using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentRuntime
{
    // The engine sees standard AIContent; city evidence stays in the record.
    internal sealed class CityFunctions
    {
        private readonly IAgentTools m_Tools;
        private readonly SessionRecord m_Record;
        private readonly Action<SessionUpdate> m_Update;
        private readonly HashSet<string> m_Names = new HashSet<string>(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<Microsoft.Agents.AI.AgentSession, Calls> m_Calls = new ConditionalWeakTable<Microsoft.Agents.AI.AgentSession, Calls>();
        public CityFunctions(IAgentTools tools, SessionRecord record, Action<SessionUpdate> update)
        {
            m_Tools = tools;
            m_Record = record;
            m_Update = update;
        }

        private sealed class Calls
        {
            public readonly string Prefix = Guid.NewGuid().ToString("N");
        }

        public string Id(Microsoft.Agents.AI.AgentSession session, string call) => m_Calls.GetOrCreateValue(session).Prefix + "-" + call;
        public bool Contains(string name) => m_Names.Contains(name);
        public IList<AITool> List(bool vision)
        {
            var tools = new List<AITool>();
            foreach (var item in m_Tools.List(vision))
            {
                m_Names.Add(item.Name);
                tools.Add(new Function(this, item));
            }
            return tools;
        }
        private async ValueTask<object> InvokeAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            var invocation = FunctionInvokingChatClient.CurrentContext;
            var session = AIAgent.CurrentRunContext.Session;
            string rawCall = invocation.CallContent.CallId;
            string call = Id(session, rawCall);
            var watch = Stopwatch.StartNew();
            var submitted = m_Record.Clock();
            m_Record.FunctionStart(call, name);
            m_Update(new SessionUpdate { Kind = "tool", CallId = call, Tool = name, Text = arguments, Status = AgentStatus.Working });
            AgentToolResult result = null;
            try
            {
                result = await m_Tools.InvokeAsync(name, arguments, cancellationToken).ConfigureAwait(false);
                if (result.ImagePng == null) return result.Text;
                var content = new List<AIContent>
                {
                    new TextContent(result.Text ?? "")
                };
                if (!string.IsNullOrEmpty(result.ImageCaption)) content.Add(new TextContent(result.ImageCaption));
                content.Add(new DataContent(result.ImagePng, "image/png"));
                return content.ToArray();
            }
            catch (Exception error)
            {
                result = new AgentToolResult
                {
                    Success = false,
                    Text = error is OperationCanceledException ? "tool call interrupted" : error.Message
                };
                throw;
            }
            finally
            {
                m_Record.Function(call, name, arguments, result, watch.ElapsedMilliseconds, new ToolAnchors { Submitted = submitted, Applied = result.Applied, Completed = m_Record.Clock(), QueuedMs = result.QueuedMs }, result.Success ? null : result.Text, AIAgent.CurrentRunContext.Agent.Name);
                m_Update(new SessionUpdate { Kind = "tool", CallId = call, Tool = name, Text = arguments, Result = result.Text, Status = result.Success ? AgentStatus.Idle : AgentStatus.Error, Image = ToolPreview.DataUri(result.PreviewJpeg) });
            }
        }

        private sealed class Function : AIFunction
        {
            private readonly CityFunctions m_Owner;
            private readonly AgentToolDeclaration m_Declaration;
            private readonly JsonElement m_Schema;
            public Function(CityFunctions owner, AgentToolDeclaration declaration)
            {
                m_Owner = owner;
                m_Declaration = declaration;
                using (var json = JsonDocument.Parse(declaration.ParametersJson))
                    m_Schema = json.RootElement.Clone();
            }

            public override string Name => m_Declaration.Name;
            public override string Description => m_Declaration.Description;
            public override JsonElement JsonSchema => m_Schema;

            protected override ValueTask<object> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => m_Owner.InvokeAsync(Name, JsonSerializer.Serialize(arguments), cancellationToken);
        }
    }
}
