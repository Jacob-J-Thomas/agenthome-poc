using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationRecordingAuthenticatedWakeVerifier : IGovernedLoopAuthenticatedWakeVerificationPort
{
    internal List<string> References { get; } = [];

    public Task<GovernedLoopAuthenticatedWakeVerificationResult?> VerifyAsync(
        GovernedLoopAuthenticatedWakeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        References.Add(request.AuthenticatedEventReference);
        return Task.FromResult<GovernedLoopAuthenticatedWakeVerificationResult?>(null);
    }
}
