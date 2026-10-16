using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AgentRuntime
{
    internal sealed class AgentToolExecutor
    {
        private readonly AgentToolSurface m_ToolSurface;
        private readonly AgentClientFactory m_ClientFactory;
        private readonly AgentObservability m_Observability;
        private readonly Action<SessionUpdate> m_Emit;
        private readonly Action<ChatMessage> m_AppendHistory;

        public AgentToolExecutor(AgentToolSurface toolSurface, AgentClientFactory clientFactory,
            AgentObservability observability, Action<SessionUpdate> emit,
            Action<ChatMessage> appendHistory)
        {
            m_ToolSurface = toolSurface;
            m_ClientFactory = clientFactory;
            m_Observability = observability;
            m_Emit = emit;
            m_AppendHistory = appendHistory;
        }

        public int FunctionCount { get; private set; }

        public void Reset()
        {
            FunctionCount = 0;
        }

        public async Task ExecuteAsync(IReadOnlyList<FunctionCallContent> toolCalls, CancellationToken cancellationToken)
        {
            m_Emit(new SessionUpdate { Kind = "status", Status = AgentStatus.Working });
            // Image previews ride after every tool result of this generation:
            // interleaving them between tool messages breaks the Chat
            // Completions pairing (assistant tool_calls must be followed by
            // consecutive tool results).
            var pendingImages = new List<AgentToolResult>();
            for (int index = 0; index < toolCalls.Count; index++)
            {
                FunctionCallContent call = toolCalls[index];
                string argumentsJson = SerializeArguments(call.Arguments);
                Stopwatch timer = Stopwatch.StartNew();
                SimulationStamp submitted = m_Observability.Clock();
                m_Observability.FunctionStart(call.CallId, call.Name);
                m_Emit(new SessionUpdate { Kind = "tool", Tool = call.Name ?? call.CallId, Text = argumentsJson });
                AgentToolResult result;
                try
                {
                    result = await InvokeAsync(call.Name, argumentsJson, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    timer.Stop();
                    RecordInterrupted(call, argumentsJson, timer.ElapsedMilliseconds, submitted);
                    PoisonRemaining(toolCalls, index + 1);
                    throw;
                }
                timer.Stop();
                FunctionCount++;
                m_Observability.Function(call.CallId, call.Name, argumentsJson, result.Text, result.Success,
                    timer.ElapsedMilliseconds,
                    new ToolAnchors
                    {
                        Submitted = submitted,
                        Applied = result.Applied,
                        Completed = m_Observability.Clock(),
                        QueuedMs = result.QueuedMs,
                    },
                    result.Success ? null : result.Text);
                // Completion pairs with the start event above by arrival order.
                // Status carries only the outcome color for the tool row.
                m_Emit(new SessionUpdate
                {
                    Kind = "tool",
                    Tool = call.Name ?? call.CallId,
                    Text = TruncateToolText(result.Text),
                    Status = result.Success ? AgentStatus.Idle : AgentStatus.Error,
                    Image = ToolPreview.DataUri(result.PreviewJpeg),
                });
                m_AppendHistory(new ChatMessage(ChatRole.Tool,
                    new List<AIContent> { new FunctionResultContent(call.CallId, result.Text) }));
                if (result.ImagePng != null && result.ImagePng.Length > 0)
                {
                    pendingImages.Add(result);
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    PoisonRemaining(toolCalls, index + 1);
                    foreach (AgentToolResult pending in pendingImages)
                    {
                        AppendToolImage(pending);
                    }
                    throw new OperationCanceledException(cancellationToken);
                }
            }
            foreach (AgentToolResult pending in pendingImages)
            {
                AppendToolImage(pending);
            }
        }

        private void RecordInterrupted(
            FunctionCallContent call, string argumentsJson, long elapsedMilliseconds, SimulationStamp submitted)
        {
            FunctionCount++;
            m_Observability.Function(call.CallId, call.Name, argumentsJson, "tool call interrupted", false,
                elapsedMilliseconds,
                new ToolAnchors { Submitted = submitted, Completed = m_Observability.Clock() },
                "interrupted");
            m_AppendHistory(new ChatMessage(ChatRole.Tool,
                new List<AIContent> { new FunctionResultContent(call.CallId, "tool call interrupted") }));
        }

        /// <summary>
        /// Every remaining call in this batch still gets a result, or the
        /// orphaned tool_calls break Chat Completions pairing. No UI events
        /// and no start events: these calls never ran.
        /// </summary>
        private void PoisonRemaining(IReadOnlyList<FunctionCallContent> toolCalls, int start)
        {
            for (int rest = start; rest < toolCalls.Count; rest++)
            {
                FunctionCallContent skipped = toolCalls[rest];
                RecordInterrupted(skipped, SerializeArguments(skipped.Arguments), 0, null);
            }
        }

        internal static string SerializeArguments(IDictionary<string, object> arguments)
        {
            return arguments == null || arguments.Count == 0 ? "{}" : JsonSerializer.Serialize(arguments);
        }

        private static string TruncateToolText(string text)
        {
            const int MaxToolTextLength = 800;
            if (string.IsNullOrEmpty(text) || text.Length <= MaxToolTextLength)
            {
                return text ?? "";
            }
            return text.Substring(0, MaxToolTextLength) + "…";
        }

        private async Task<AgentToolResult> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken)
        {
            try
            {
                if (!m_ToolSurface.IsListed(name, m_ClientFactory.GetProfile()))
                {
                    return Error("tool is not available for this model or current settings");
                }
                return await m_ToolSurface.InvokeAsync(name, argumentsJson, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                m_Observability.Error("tool", e.ToString());
                return Error(AgentObservability.RedactSecrets(e.Message));
            }
        }

        private void AppendToolImage(AgentToolResult result)
        {
            byte[] image = result.ImagePng;
            if (image == null || image.Length == 0 || !m_ClientFactory.GetProfile().VisionAvailable)
            {
                return;
            }
            if (image.Length > 8 * 1024 * 1024)
            {
                m_Observability.Error("vision-attach", "image exceeds attachment limit");
                return;
            }
            string caption = string.IsNullOrWhiteSpace(result.ImageCaption)
                ? "Image returned by the tool."
                : result.ImageCaption;
            m_AppendHistory(new ChatMessage(ChatRole.User, new List<AIContent>
            {
                new TextContent(caption),
                new DataContent(new ReadOnlyMemory<byte>(image), "image/png"),
            }));
        }

        private static AgentToolResult Error(string message)
        {
            return new AgentToolResult { Success = false, Text = JsonSerializer.Serialize(new { error = message }) };
        }
    }
}
