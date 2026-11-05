using System;
using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentRuntime
{
    /// <summary>One built-in session's fixed Responses connection.</summary>
    public sealed class ModelConnection
    {
        public string Endpoint;
        public string ApiKey;
        public string Model;
        public int WindowTokens = 128_000;
        public int OutputTokens = 16_384;

        public IChatClient Open()
        {
            if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Model))
            {
                throw new InvalidOperationException("Set an API key and choose a model in settings, then apply.");
            }
            return new OpenAIClient(new ApiKeyCredential(ApiKey), new OpenAIClientOptions
            {
                Endpoint = new Uri(Endpoint),
                NetworkTimeout = TimeSpan.FromMinutes(10),
            }).GetResponsesClient().AsIChatClient(Model);
        }
    }
}
