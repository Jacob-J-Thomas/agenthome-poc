using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

internal sealed class StubGovernedLoopAuthenticatedWakeVerificationPort : IGovernedLoopAuthenticatedWakeVerificationPort
{
    internal GovernedLoopAuthenticatedWakeVerificationResult? Result { get; set; }

    internal Exception? Exception { get; set; }

    internal bool ReturnNull { get; set; }

    internal int VerifyCount { get; private set; }

    internal GovernedLoopAuthenticatedWakeVerificationRequest? LastRequest { get; private set; }

    public Task<GovernedLoopAuthenticatedWakeVerificationResult?> VerifyAsync(
        GovernedLoopAuthenticatedWakeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyCount++;
        LastRequest = request;
        if (Exception is not null)
        {
            throw Exception;
        }

        if (ReturnNull)
        {
            return Task.FromResult<GovernedLoopAuthenticatedWakeVerificationResult?>(null);
        }

        return Task.FromResult<GovernedLoopAuthenticatedWakeVerificationResult?>(Result
            ?? new GovernedLoopAuthenticatedWakeVerificationResult(
                GovernedLoopAuthenticatedWakeVerificationStatus.Verified,
                new GovernedLoopAuthenticatedWakeVerification(
                    request.CheckpointId,
                    request.CheckpointHash,
                    request.AuthenticatedEventReference,
                    request.AuthenticationEvidenceHash,
                    request.CheckpointPublishedAtUtc,
                    GovernedLoopSleepApplicationTestFixture.Now,
                    Eligible: true)));
    }
}
