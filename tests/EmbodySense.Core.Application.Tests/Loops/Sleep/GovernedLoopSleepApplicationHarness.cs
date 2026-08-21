using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

internal sealed class GovernedLoopSleepApplicationHarness
{
    internal GovernedLoopSleepApplicationHarness(
        GovernedLoopSleepCurrentPosture? posture = null,
        GovernedLoopWakeMode mode = GovernedLoopWakeMode.Timestamp,
        DateTimeOffset? deadlineUtc = null)
    {
        Posture = posture ?? GovernedLoopSleepApplicationTestFixture.Posture();
        Store = new InMemoryGovernedLoopSleepStore();
        CurrentPosture = new StubGovernedLoopSleepCurrentPosturePort
        {
            Result = new GovernedLoopSleepCurrentPostureReadResult(
                GovernedLoopSleepCurrentPostureReadStatus.Found,
                Posture)
        };
        Continuation = new StubGovernedLoopWakeContinuationPort();
        AuthenticatedWakeVerification = new StubGovernedLoopAuthenticatedWakeVerificationPort();
        TimeProvider = new StubGovernedLoopSleepTimeProvider(GovernedLoopSleepApplicationTestFixture.Now);
        Service = new GovernedLoopSleepService(
            Store,
            CurrentPosture,
            Continuation,
            AuthenticatedWakeVerification,
            TimeProvider);
        PublicationRequest = GovernedLoopSleepApplicationTestFixture.PublicationRequest(Posture, mode, deadlineUtc);
    }

    internal GovernedLoopSleepCurrentPosture Posture { get; }

    internal InMemoryGovernedLoopSleepStore Store { get; }

    internal StubGovernedLoopSleepCurrentPosturePort CurrentPosture { get; }

    internal StubGovernedLoopWakeContinuationPort Continuation { get; }

    internal StubGovernedLoopAuthenticatedWakeVerificationPort AuthenticatedWakeVerification { get; }

    internal StubGovernedLoopSleepTimeProvider TimeProvider { get; }

    internal GovernedLoopSleepService Service { get; }

    internal GovernedLoopSleepPublicationRequest PublicationRequest { get; }

    internal async Task<GovernedLoopSleepCheckpoint> PublishAsync()
    {
        var result = await Service.PublishAsync(PublicationRequest);
        Assert.Equal(GovernedLoopSleepPublicationStatus.Published, result.Status);
        return Assert.IsType<GovernedLoopSleepCheckpoint>(result.Checkpoint);
    }
}
