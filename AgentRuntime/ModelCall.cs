using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AgentRuntime
{
    /// <summary>
    /// One model request on its way to <c>model-calls.jsonl</c>: begun with
    /// what is sent, ended once with how it came back. Purpose is
    /// <c>turn</c> or <c>compact</c>; attempt 2 is the retry after a
    /// context-length failure.
    /// </summary>
    internal sealed class ModelCall
    {
        private readonly SessionRecord m_Record;
        private readonly JsonObject m_Data;
        private readonly Stopwatch m_Timer = Stopwatch.StartNew();
        private readonly DateTime m_RequestedUtc = DateTime.UtcNow;
        private readonly SimulationStamp m_Submitted;
        private bool m_Ended;

        private ModelCall(SessionRecord record, JsonObject data)
        {
            m_Record = record;
            m_Data = data;
            m_Submitted = record.Clock();
        }

        public string Id => (string)m_Data["id"];

        public static ModelCall Begin(
            SessionRecord record,
            string purpose,
            int attempt,
            ModelSettings settings,
            IEnumerable<ChatMessage> messages,
            ChatOptions options)
        {
            return new ModelCall(record, new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N").Substring(0, 8),
                ["purpose"] = purpose,
                ["attempt"] = attempt,
                ["model"] = new JsonObject { ["name"] = settings.Model, ["wire"] = settings.Wire.ToString() },
                ["request"] = new JsonObject
                {
                    ["messages"] = ModelCallJson.Messages(messages, record),
                    ["catalog"] = record.KeepCatalog(ModelCallJson.Catalog(options?.Tools)),
                },
            });
        }

        public void Completed(ChatResponse response, string reasoning, IEnumerable<FunctionCallContent> toolCalls)
        {
            var outcome = new JsonObject
            {
                ["status"] = "completed",
                ["model"] = response.ModelId,
                ["finishReason"] = response.FinishReason?.Value,
            };
            ModelCallJson.SetText(outcome, response.Text, m_Record);
            if (!string.IsNullOrEmpty(reasoning))
            {
                var thought = new JsonObject();
                ModelCallJson.SetText(thought, reasoning, m_Record);
                outcome["reasoning"] = thought;
            }
            outcome["toolCalls"] = ModelCallJson.ToolCalls(toolCalls);
            outcome["usage"] = AgentUsageJson.Serialize(response.Usage);
            End(outcome);
        }

        /// <summary>Status is <c>cancelled</c>, <c>timeout</c>, or <c>failed</c>.</summary>
        public void Ended(string status, Exception error = null)
        {
            var outcome = new JsonObject { ["status"] = status };
            if (error != null)
            {
                outcome["failure"] = new JsonObject
                {
                    ["type"] = error.GetType().Name,
                    ["message"] = SessionRecord.Truncate(error.Message, 8192),
                };
            }
            End(outcome);
        }

        private void End(JsonObject outcome)
        {
            if (m_Ended)
            {
                return;
            }
            m_Ended = true;
            m_Timer.Stop();
            m_Data["outcome"] = outcome;
            m_Data["timing"] = new JsonObject
            {
                ["requestedUtc"] = m_RequestedUtc.ToString("o", CultureInfo.InvariantCulture),
                ["completedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["latencyMs"] = m_Timer.ElapsedMilliseconds,
            };
            var timeline = new JsonObject();
            if (m_Submitted != null)
            {
                timeline["submitted"] = m_Submitted.ToJson();
            }
            SimulationStamp completed = m_Record.Clock();
            if (completed != null)
            {
                timeline["completed"] = completed.ToJson();
            }
            m_Data["timeline"] = timeline;
            m_Record.ModelCall(m_Data);
        }
    }

    /// <summary>
    /// Session-record form of a model request and reply. These are the
    /// client messages handed to <see cref="IChatClient"/>, not wire bytes:
    /// the request pipeline may still add fields such as echoed reasoning.
    /// A text at least <see cref="SessionRecord.BlobChars"/> long becomes a
    /// blob reference, so the system prompt and large tool results are kept
    /// once rather than on every request.
    /// </summary>
    internal static class ModelCallJson
    {
        public static JsonArray Messages(IEnumerable<ChatMessage> messages, SessionRecord record)
        {
            var array = new JsonArray();
            foreach (ChatMessage message in messages)
            {
                var content = new JsonArray();
                foreach (AIContent part in message.Contents)
                {
                    content.Add(Content(part, record));
                }
                var entry = new JsonObject { ["role"] = message.Role.Value };
                if (!string.IsNullOrEmpty(message.AuthorName))
                {
                    entry["author"] = message.AuthorName;
                }
                entry["content"] = content;
                array.Add(entry);
            }
            return array;
        }

        public static JsonArray Catalog(IList<AITool> tools)
        {
            var catalog = new JsonArray();
            if (tools == null)
            {
                return catalog;
            }
            foreach (AITool tool in tools)
            {
                var entry = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description };
                if (tool is AIFunctionDeclaration declaration)
                {
                    entry["parameters"] = JsonNode.Parse(declaration.JsonSchema.GetRawText());
                }
                catalog.Add(entry);
            }
            return catalog;
        }

        public static JsonArray ToolCalls(IEnumerable<FunctionCallContent> calls)
        {
            var array = new JsonArray();
            foreach (FunctionCallContent call in calls)
            {
                array.Add(new JsonObject
                {
                    ["id"] = call.CallId,
                    ["name"] = call.Name,
                    ["arguments"] = AgentToolExecutor.SerializeArguments(call.Arguments),
                });
            }
            return array;
        }

        /// <summary>Sets <c>text</c>, or <c>blob</c> and <c>chars</c> when the text is long and the record keeps files.</summary>
        public static void SetText(JsonObject entry, string text, SessionRecord record)
        {
            text = text ?? "";
            string blob = text.Length >= SessionRecord.BlobChars ? record.KeepBlob(text) : null;
            if (blob == null)
            {
                entry["text"] = text;
                return;
            }
            entry["blob"] = blob;
            entry["chars"] = text.Length;
        }

        private static JsonObject Content(AIContent part, SessionRecord record)
        {
            switch (part)
            {
                case TextContent text:
                {
                    var entry = new JsonObject { ["type"] = "text" };
                    SetText(entry, text.Text, record);
                    return entry;
                }
                case TextReasoningContent reasoning:
                {
                    var entry = new JsonObject { ["type"] = "reasoning" };
                    SetText(entry, reasoning.Text, record);
                    return entry;
                }
                case FunctionCallContent call:
                    return new JsonObject
                    {
                        ["type"] = "tool_call",
                        ["id"] = call.CallId,
                        ["name"] = call.Name,
                        ["arguments"] = AgentToolExecutor.SerializeArguments(call.Arguments),
                    };
                case FunctionResultContent result:
                {
                    var entry = new JsonObject { ["type"] = "tool_result", ["id"] = result.CallId };
                    SetText(entry, result.Result?.ToString(), record);
                    return entry;
                }
                case DataContent data:
                {
                    byte[] bytes = data.Data.ToArray();
                    return new JsonObject
                    {
                        ["type"] = "data",
                        ["mediaType"] = data.MediaType,
                        ["bytes"] = bytes.Length,
                        ["sha256"] = SessionRecord.Sha256(bytes),
                    };
                }
                default:
                    return new JsonObject { ["type"] = part.GetType().Name };
            }
        }
    }
}
