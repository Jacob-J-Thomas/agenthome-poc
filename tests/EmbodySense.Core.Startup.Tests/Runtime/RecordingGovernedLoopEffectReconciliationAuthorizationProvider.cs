using EmbodySense.Core.Startup.Loops.Execution.Reconciliation;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class RecordingGovernedLoopEffectReconciliationAuthorizationProvider : IGovernedLoopEffectReconciliationAuthorizationProvider
{
    internal GovernedLoopEffectReconciliationAuthorizationStatus Status { get; set; } = GovernedLoopEffectReconciliationAuthorizationStatus.Ready;

    internal GovernedLoopEffectReconciliationAuthorizationRequest? LastRequest { get; private set; }

    internal int Calls { get; private set; }

    internal bool Throw { get; set; }

    internal bool MismatchRequestHash { get; set; }

    internal Action<int>? OnCall { get; set; }

    public Task<GovernedLoopEffectReconciliationAuthorizationResult> AuthorizeAsync(GovernedLoopEffectReconciliationAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        OnCall?.Invoke(Calls);
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        if (Throw)
        {
            throw new IOException("The test authority boundary is unavailable.");
        }

        var requestHash = MismatchRequestHash ? GovernedLoopEffectReconciliationStartupTestFixture.Hash("mismatched-request") : request.RequestHash;
        return Task.FromResult(Status == GovernedLoopEffectReconciliationAuthorizationStatus.Ready
            ? new GovernedLoopEffectReconciliationAuthorizationResult(Status, requestHash, "actor-reconciliation", "scope-reconciliation", GovernedLoopEffectReconciliationStartupTestFixture.Hash("authority-evidence"))
            : new GovernedLoopEffectReconciliationAuthorizationResult(Status, requestHash));
    }
}
