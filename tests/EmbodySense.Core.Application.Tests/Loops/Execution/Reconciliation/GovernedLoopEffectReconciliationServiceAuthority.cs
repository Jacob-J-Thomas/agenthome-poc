using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationServiceAuthority : IGovernedLoopEffectReconciliationAuthorizationSource
{
    internal GovernedLoopEffectReconciliationAuthorizationStatus Status { get; set; } = GovernedLoopEffectReconciliationAuthorizationStatus.Ready;

    internal int Calls { get; private set; }

    internal bool ThrowOnAuthorize { get; set; }

    internal bool ReturnNullOnAuthorize { get; set; }

    internal Func<GovernedLoopEffectReconciliationAuthorizationRequest, GovernedLoopEffectReconciliationAuthorizationResult>? ResultFactory { get; set; }

    public Task<GovernedLoopEffectReconciliationAuthorizationResult> AuthorizeAsync(GovernedLoopEffectReconciliationAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (ThrowOnAuthorize)
        {
            throw new IOException("The test authority is unavailable.");
        }

        if (ReturnNullOnAuthorize)
        {
            return Task.FromResult<GovernedLoopEffectReconciliationAuthorizationResult>(null!);
        }

        if (ResultFactory is not null)
        {
            return Task.FromResult(ResultFactory(request));
        }

        var evidence = Status == GovernedLoopEffectReconciliationAuthorizationStatus.Ready
            ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            : null;
        return Task.FromResult(new GovernedLoopEffectReconciliationAuthorizationResult(Status, request.Purpose, request.Case, request.Binding, evidence));
    }
}
