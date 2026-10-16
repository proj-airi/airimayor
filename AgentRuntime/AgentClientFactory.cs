using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace AgentRuntime
{
    /// <summary>
    /// Owns the OpenAI-compatible client cache and the resolved model profile.
    /// The request shape follows <see cref="ModelSettings.Wire"/>; the token
    /// window follows <see cref="ModelSettings.WindowTokens"/>. Model names
    /// are never parsed.
    /// </summary>
    internal sealed class AgentClientFactory : IDisposable
    {
        internal const int ModelRequestTimeoutSeconds = 600;
        internal static readonly TimeSpan ModelRequestTimeout =
            TimeSpan.FromSeconds(ModelRequestTimeoutSeconds);

        private readonly SessionRecord m_Record;
        private readonly string m_SessionId;
        private readonly Func<ModelSettings> m_ReadModel;
        private readonly IChatClient m_Override;
        private readonly Func<IReadOnlyList<ReasoningEchoSnapshot>> m_ReasoningSnapshots;
        private readonly object m_Lock = new object();
        private IChatClient m_Client;
        private AgentModelProfile m_Profile;
        private string m_ConfigSignature;

        public AgentClientFactory(
            SessionRecord record,
            string sessionId,
            Func<ModelSettings> readModel,
            Func<IReadOnlyList<ReasoningEchoSnapshot>> reasoningSnapshots = null,
            IChatClient chatClient = null)
        {
            m_Record = record;
            m_SessionId = sessionId;
            m_ReadModel = readModel;
            m_ReasoningSnapshots = reasoningSnapshots;
            m_Override = chatClient;
        }

        public IChatClient GetClient()
        {
            lock (m_Lock)
            {
                RefreshConfigurationLocked();
                if (m_Override != null)
                {
                    return m_Override;
                }
                if (m_Client != null)
                {
                    return m_Client;
                }
                ModelSettings settings = Read();
                if (string.IsNullOrWhiteSpace(settings.Endpoint) ||
                    string.IsNullOrWhiteSpace(settings.ApiKey) ||
                    string.IsNullOrWhiteSpace(settings.Model))
                {
                    return null;
                }

                try
                {
                    var options = new OpenAIClientOptions
                    {
                        Endpoint = new Uri(settings.Endpoint),
                        NetworkTimeout = ModelRequestTimeout,
                    };
                    options.AddPolicy(
                        new ConversationHeaderPolicy(m_SessionId),
                        PipelinePosition.PerCall);
                    options.AddPolicy(
                        new ReasoningEchoPolicy(m_ReasoningSnapshots, OnReasoningEchoed),
                        PipelinePosition.PerCall);
                    var openAiClient = new OpenAIClient(
                        new ApiKeyCredential(settings.ApiKey),
                        options);
                    if (settings.Wire == ModelWire.Responses)
                    {
#pragma warning disable OPENAI001
                        m_Client = openAiClient.GetResponsesClient().AsIChatClient(settings.Model);
#pragma warning restore OPENAI001
                    }
                    else
                    {
                        ChatClient chatClient = openAiClient.GetChatClient(settings.Model);
                        m_Client = chatClient.AsIChatClient();
                    }
                    return m_Client;
                }
                catch (Exception e)
                {
                    m_Record.Error("client-create", e.ToString());
                    return null;
                }
            }
        }

        public AgentModelProfile GetProfile()
        {
            lock (m_Lock)
            {
                RefreshConfigurationLocked();
                return m_Profile;
            }
        }

        public void Refresh()
        {
            lock (m_Lock)
            {
                m_Client?.Dispose();
                m_Client = null;
                m_Profile = null;
                m_ConfigSignature = null;
            }
        }

        private void OnReasoningEchoed(int injected)
        {
            m_Record?.Record("reasoning-echo", new JsonObject
            {
                ["injected"] = injected,
            });
        }

        private void RefreshConfigurationLocked()
        {
            ModelSettings settings = Read();
            string signature = settings.Endpoint + "|" +
                settings.ApiKey + "|" + settings.Model + "|" +
                settings.WindowTokens + "|" + settings.Vision + "|" +
                settings.Wire;
            if (string.Equals(m_ConfigSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            m_Client?.Dispose();
            m_Client = null;
            m_ConfigSignature = signature;
            m_Profile = AgentModelProfile.Resolve(
                settings.WindowTokens,
                settings.Vision,
                settings.Wire.ToString());
        }

        private ModelSettings Read()
        {
            return m_ReadModel();
        }

        public void Dispose()
        {
            lock (m_Lock)
            {
                m_Client?.Dispose();
                m_Client = null;
            }
        }
    }
}
