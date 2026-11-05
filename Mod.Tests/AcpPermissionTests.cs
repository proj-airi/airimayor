using airimayor.Agent;
using airimayor.Host;
using dotacp.protocol;
using Xunit;

namespace airimayor.Tests
{
    public sealed class AcpPermissionTests
    {
        [Fact]
        public async Task Permission_requests_do_not_require_a_city_tool_name_and_prefer_allow_once()
        {
            using AcpSession session = await OpenSessionAsync();
            RequestPermissionResponse result = await session.RequestPermissionAsync(new RequestPermissionRequest
            {
                SessionId = "",
                Options = new[]
                {
                    new PermissionOption { OptionId = "always", Kind = PermissionOptionKind.AllowAlways },
                    new PermissionOption { OptionId = "once", Kind = PermissionOptionKind.AllowOnce },
                },
            });
            Assert.Equal("once", Assert.IsType<SelectedPermissionOutcome>(result.Outcome).OptionId.ToString());
        }

        [Fact]
        public async Task Unknown_sessions_and_cancelled_requests_are_still_cancelled()
        {
            using AcpSession session = await OpenSessionAsync();
            var request = new RequestPermissionRequest
            {
                SessionId = "another-session",
                Options = new[] { new PermissionOption { OptionId = "once", Kind = PermissionOptionKind.AllowOnce } },
            };
            Assert.IsType<RequestPermissionOutcomeCancelled>((await session.RequestPermissionAsync(request)).Outcome);
            request.SessionId = "";
            Assert.IsType<RequestPermissionOutcomeCancelled>((await session.RequestPermissionAsync(request, new CancellationToken(true))).Outcome);
        }

        private static async Task<AcpSession> OpenSessionAsync()
        {
            var session = new AcpSession(null, "City work.", () => false,
                new AcpRegistry(Path.GetTempPath()), "sample", null, Path.GetTempPath(), null, null);
            // No tool-server executable: initialization ends before starting an agent.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (session.Status != AgentStatus.Error)
                await Task.Delay(10, timeout.Token);
            return session;
        }
    }
}
