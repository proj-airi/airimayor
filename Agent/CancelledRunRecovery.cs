using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using AgentSession = Microsoft.Agents.AI.AgentSession;

namespace airimayor.Agent
{
    // SDK 1.27 persists successful service calls, but not tool results before a failed next call.
    // Apply the same repair to root and native background sessions, including SDK tool results.
    internal sealed class CancelledRunRecovery
    {
        private readonly InMemoryChatHistoryProvider m_History;
        private readonly AIAgent m_Agent;
        private readonly ConditionalWeakTable<AgentSession, Dictionary<string, object>> m_Results = new ConditionalWeakTable<AgentSession, Dictionary<string, object>>();
        private CancelledRunRecovery(AIAgent agent, InMemoryChatHistoryProvider history)
        {
            m_Agent = agent;
            m_History = history;
        }

        public static AIAgent Wrap(AIAgent agent, InMemoryChatHistoryProvider history)
        {
            var recovery = new CancelledRunRecovery(agent, history);
            return agent.AsBuilder().Use(recovery.FunctionAsync).Use(recovery.RunAsync, recovery.RunStreamingAsync).Build();
        }

        private async ValueTask<object> FunctionAsync(AIAgent agent, FunctionInvocationContext context, Func<FunctionInvocationContext, CancellationToken, ValueTask<object>> next, CancellationToken cancellationToken)
        {
            object result = await next(context, cancellationToken).ConfigureAwait(false);
            m_Results.GetOrCreateValue(AIAgent.CurrentRunContext.Session)[context.CallContent.CallId] = result;
            return result;
        }

        private async Task<AgentResponse> RunAsync(IEnumerable<ChatMessage> input, AgentSession session, AgentRunOptions options, AIAgent next, CancellationToken cancellationToken)
        {
            var messages = input.ToList();
            try
            {
                return await next.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RepairAsync(messages, session).ConfigureAwait(false);
                throw;
            }
            finally
            {
                m_Results.GetOrCreateValue(session).Clear();
            }
        }

        // The SDK's shared middleware cancels its channel reader before its producer has finished recovery.
        private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(IEnumerable<ChatMessage> input, AgentSession session,
            AgentRunOptions options, AIAgent next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var messages = input.ToList();
            try
            {
                await using var updates = next.RunStreamingAsync(messages, session, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool more;
                    try
                    {
                        more = await updates.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        await RepairAsync(messages, session).ConfigureAwait(false);
                        throw;
                    }
                    if (!more) break;
                    yield return updates.Current;
                }
            }
            finally
            {
                m_Results.GetOrCreateValue(session).Clear();
            }
        }

        private async Task RepairAsync(IReadOnlyList<ChatMessage> messages, AgentSession session)
        {
            var history = m_History.GetMessages(session).ToList();
            var results = m_Results.GetOrCreateValue(session);
            var paired = new HashSet<string>(history.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Select(item => item.CallId));
            foreach (var call in history.SelectMany(item => item.Contents).OfType<FunctionCallContent>().ToArray())
                if (paired.Add(call.CallId))
                    history.Add(new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent(call.CallId, results.TryGetValue(call.CallId, out var result) ? result : "tool call interrupted") }));
            var pending = await m_Agent.GetService<MessageInjectingChatClient>().GetPendingMessagesAsync(session).ConfigureAwait(false);
            foreach (var message in messages)
                if (!history.Any(item => ReferenceEquals(item, message) || message.MessageId != null && item.MessageId == message.MessageId) && !pending.Contains(message))
                    history.Add(message);
            m_History.SetMessages(session, history);
            // The SDK's compaction index still describes the history before this repair.
            session.StateBag.TryRemoveValue(nameof(ContextWindowCompactionStrategy));
        }
    }
}
