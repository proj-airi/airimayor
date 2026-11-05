using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentRuntime
{
    internal sealed class NativeHead
    {
        public AIAgent Agent { get; }
        public Microsoft.Agents.AI.AgentSession Session { get; private set; }
        public InMemoryChatHistoryProvider History { get; }

        public NativeHead(IChatClient client, IList<AITool> tools, string instructions, int window, int outputTokens, ILoggerFactory logger, IEnumerable<AIAgent> children = null, string name = "mayor")
        {
            var compaction = new ContextWindowCompactionStrategy(window, outputTokens);
            History = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions { ChatReducer = compaction.AsChatReducer() });
            var agent = new HarnessAgent(client, new HarnessAgentOptions
            {
                Name = name,
                HarnessInstructions = "",
                DisableWebSearch = true,
                DisableFileMemory = true,
                DisableAgentSkillsProvider = true,
                DisableAgentModeProvider = true,
                DisableToolAutoApproval = true,
                DisableApprovalResponseBinding = true,
                DisableApprovalNotRequiredFunctionBypassing = true,
                DisableOpenTelemetry = true,
                ChatHistoryProvider = History,
                CompactionStrategy = compaction,
                MaxContextWindowTokens = window,
                MaxOutputTokens = outputTokens,
                ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools, MaxOutputTokens = outputTokens },
                BackgroundAgents = children,
            }, logger);
            Agent = CancelledRunRecovery.Wrap(agent, History);
        }

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            Session = await Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        public IAsyncEnumerable<AgentResponseUpdate> RunAsync(IEnumerable<ChatMessage> input, CancellationToken cancellationToken) => Agent.RunStreamingAsync(input, Session, cancellationToken: cancellationToken);
        public MessageInjectingChatClient Injection => Agent.GetService<MessageInjectingChatClient>();

        public async Task ReleaseAsync()
        {
            var background = Agent.GetService<BackgroundAgentsProvider>();
            if (background != null && Session != null)
                await background.ReleaseSessionAsync(Session).ConfigureAwait(false);
        }
    }
}
