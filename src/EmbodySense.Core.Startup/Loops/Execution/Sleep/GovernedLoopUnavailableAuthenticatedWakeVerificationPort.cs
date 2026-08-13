using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Fails closed until an authenticated-event adapter is explicitly composed by its owning surface.</summary>
internal sealed class GovernedLoopUnavailableAuthenticatedWakeVerificationPort : IGovernedLoopAuthenticatedWakeVerificationPort
{
    public Task<GovernedLoopAuthenticatedWakeVerificationResult?> VerifyAsync(
        GovernedLoopAuthenticatedWakeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<GovernedLoopAuthenticatedWakeVerificationResult?>(
            new GovernedLoopAuthenticatedWakeVerificationResult(
                GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable));
    }
}
