using System.Text;
using System.Threading.Tasks;

namespace airimayor.Agent
{
    /// <summary>
    /// The settings connection check waits for one session turn.
    /// </summary>
    public static class SessionTurn
    {
        public static async Task<SessionTurnResult> RunAsync(
            IAgentSession session,
            string prompt)
        {
            var done = new TaskCompletionSource<SessionTurnResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new StringBuilder();
            void OnUpdate(SessionUpdate update)
            {
                if (update == null || done.Task.IsCompleted)
                {
                    return;
                }
                if (update.Kind == "delta")
                {
                    lock (reply)
                    {
                        reply.Append(update.Text ?? "");
                    }
                }
                else if (update.Kind == "error" || update.Kind == "turn")
                {
                    string text;
                    lock (reply)
                    {
                        text = reply.ToString();
                    }
                    done.TrySetResult(new SessionTurnResult
                    {
                        Failed = update.Kind == "error",
                        Error = update.Text ?? "",
                        Reply = text,
                    });
                }
                else if (update.Kind == "status" && update.Status == AgentStatus.Interrupted)
                {
                    done.TrySetCanceled();
                }
            }
            session.Updated += OnUpdate;
            try
            {
                session.Prompt(prompt);
                return await done.Task.ConfigureAwait(false);
            }
            finally
            {
                session.Updated -= OnUpdate;
            }
        }
    }

    public sealed class SessionTurnResult
    {
        public bool Failed;
        public string Error;
        public string Reply;
    }
}
