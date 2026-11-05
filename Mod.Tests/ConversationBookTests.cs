using System.Threading;
using System.Threading.Tasks;
using airimayor.Agent;
using Xunit;

namespace airimayor.Agent
{
    public sealed class ConversationBookTests
    {
        [Fact]
        public async Task Task_returns_while_the_conversation_is_still_running()
        {
            var started = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            string delivered = null;
            var book = new ConversationBook(async (id, message, cancellationToken) =>
            {
                started.TrySetResult(true);
                await release.Task;
                return "studied " + message;
            }, _ => { }, text =>
            {
                delivered = text;
            });

            AgentToolResult created = await book.InvokeAsync(
                "task",
                "{\"description\":\"Tiles\",\"prompt\":\"look around\"}",
                CancellationToken.None);

            Assert.True(created.Success);
            Assert.Contains("working", created.Text);
            Assert.True(started.Task.IsCompleted);
            Assert.Equal("task", Assert.Single(book.List()).Name);

            AgentToolResult missing = await book.InvokeAsync("nope", "{}", CancellationToken.None);
            Assert.False(missing.Success);

            release.TrySetResult(true);
            for (int i = 0; i < 50 && delivered == null; i++)
            {
                await Task.Delay(20);
            }
            Assert.Equal("Tiles\nstudied look around", delivered);
        }

        [Fact]
        public async Task Cancel_all_drops_the_report()
        {
            var release = new TaskCompletionSource<bool>();
            string delivered = null;
            var book = new ConversationBook(async (id, message, cancellationToken) =>
            {
                await release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return "studied";
            }, _ => { }, text => delivered = text);

            await book.InvokeAsync(
                "task",
                "{\"description\":\"Tiles\",\"prompt\":\"look around\"}",
                CancellationToken.None);
            book.CancelAll();
            release.TrySetResult(true);
            await Task.Delay(100);
            Assert.Null(delivered);
        }
    }
}
