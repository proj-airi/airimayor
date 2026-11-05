using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace airimayor.Agent
{
    /// <summary>
    /// One prompt on an existing session, then stop. The child conversation
    /// and the settings check both wait on the session's own updates.
    /// </summary>
    public static class SessionTurn
    {
        public static async Task<SessionTurnResult> RunAsync(
            IAgentSession session,
            string prompt,
            Action<SessionUpdate> onTool,
            CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource<SessionTurnResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new StringBuilder();
            session.Updated += update =>
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
                else if (update.Kind == "tool")
                {
                    onTool?.Invoke(update);
                }
                else if (update.Kind == "error" || update.Kind == "turn")
                {
                    string text;
                    lock (reply)
                    {
                        text = reply.ToString();
                    }
                    if (done.TrySetResult(new SessionTurnResult
                    {
                        Failed = update.Kind == "error",
                        Error = update.Text ?? "",
                        Reply = text,
                    }))
                    {
                        session.Cancel();
                    }
                }
                else if (update.Kind == "status" && update.Status == AgentStatus.Interrupted)
                {
                    done.TrySetCanceled();
                }
            };
            using (cancellationToken.Register(() => session.Cancel()))
            {
                session.Prompt(prompt);
                return await done.Task.ConfigureAwait(false);
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
