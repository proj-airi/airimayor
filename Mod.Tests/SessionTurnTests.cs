using System;
using System.Threading.Tasks;
using airimayor.Agent;
using Xunit;

namespace airimayor.Tests
{
    public class SessionTurnTests
    {
        [Fact]
        public async Task ReplyComesFromTheSessionTurn()
        {
            var session = new ScriptedSession(
                new SessionUpdate { Kind = "delta", Text = "Hello." },
                new SessionUpdate { Kind = "turn" });

            SessionTurnResult result = await SessionTurn.RunAsync(
                session, "Reply in one sentence.");

            Assert.False(result.Failed);
            Assert.Equal("Hello.", result.Reply);
            Assert.Equal(0, session.Cancels);
            Assert.Equal(0, session.Subscribers);
        }

        [Fact]
        public async Task ErrorComesFromTheSession()
        {
            var session = new ScriptedSession(
                new SessionUpdate { Kind = "error", Text = "Model not configured." });

            SessionTurnResult result = await SessionTurn.RunAsync(
                session, "Reply in one sentence.");

            Assert.True(result.Failed);
            Assert.Equal("Model not configured.", result.Error);
            Assert.Equal(0, session.Cancels);
            Assert.Equal(0, session.Subscribers);
        }

        private sealed class ScriptedSession : IAgentSession
        {
            private readonly SessionUpdate[] m_Updates;

            public ScriptedSession(params SessionUpdate[] updates)
            {
                m_Updates = updates;
            }

            public int Cancels { get; private set; }

            public event Action<SessionUpdate> Updated;
            public int Subscribers => Updated?.GetInvocationList().Length ?? 0;

            public SessionRecord Record => null;

            public void Prompt(string text)
            {
                foreach (SessionUpdate update in m_Updates)
                {
                    Updated?.Invoke(update);
                }
            }

            public void Prompt(string modelText, string displayText, string placesJson)
            {
                Prompt(modelText);
            }

            public void Cancel()
            {
                Cancels++;
            }

            public string ChatStateJson()
            {
                return "{}";
            }

            public void Dispose()
            {
            }
        }
    }
}
