using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Owns one durable, single-use actuator dispatch boundary.</summary>
internal sealed class GovernedActuatorDispatchBoundary(
    IGovernedLoopEffectAttemptStore store,
    IGovernedLoopEffectAttemptLease lease,
    GovernedLoopEffectAttempt current,
    GovernedActuatorOperationDescriptor descriptor,
    TimeProvider timeProvider) : IGovernedActuatorDispatchBoundary
{
    private int _crossed;

    internal GovernedLoopEffectAttempt Current { get; private set; } = current;

    internal GovernedActuatorExternalOutcome? ObservedOutcome { get; private set; }

    /// <inheritdoc />
    public async Task<GovernedActuatorExternalOutcome> CrossAsync(
        Func<CancellationToken, Task<GovernedActuatorExternalOutcome>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Interlocked.Exchange(ref _crossed, 1) != 0)
        {
            throw new InvalidOperationException("An actuator adapter may cross its irreversible boundary at most once.");
        }

        var crossed = GovernedLoopEffectAttemptContract.Advance(
            Current,
            GovernedLoopEffectPhase.DispatchBoundaryReached,
            GovernedLoopEffectOutcome.OutcomeUnknown,
            GovernedLoopEffectEvidenceStatus.Pending,
            null,
            null,
            UtcNowOrThrow(Current.Payload.UpdatedAtUtc));
        GovernedLoopEffectAttemptStoreResult? stored;
        try
        {
            stored = await store.CompareExchangeAsync(Current.ContentHash, crossed, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new GovernedLoopEffectAttemptEvidenceException("Dispatch-boundary evidence was not durable; the external callback was not invoked.");
        }
        if (stored?.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
            || stored.Attempt is null
            || stored.Lease is not null
            || GovernedLoopEffectAttemptContract.Validate(stored.Attempt) is not null
            || !string.Equals(stored.Attempt.ContentHash, crossed.ContentHash, StringComparison.Ordinal))
        {
            throw new GovernedLoopEffectAttemptEvidenceException("Dispatch-boundary evidence was not durable; the external callback was not invoked.");
        }

        Current = stored.Attempt;
        var outcome = await callback(cancellationToken).ConfigureAwait(false);
        if (!IsCompleteOutcome(outcome))
        {
            throw new InvalidOperationException("The actuator callback returned an incomplete or malformed external outcome.");
        }

        ObservedOutcome = outcome;
        return outcome;
    }

    private bool IsCompleteOutcome(GovernedActuatorExternalOutcome? outcome)
        => outcome is not null
            && outcome.Outcome is GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed
            && (!descriptor.RequiresOutcomeEvidence || !string.IsNullOrEmpty(outcome.OutcomeEvidenceId))
            && (!descriptor.RequiresAfterEvidence || !string.IsNullOrEmpty(outcome.AfterEvidenceId))
            && IsIdentifier(outcome.OutcomeEvidenceId)
            && (outcome.AfterEvidenceId is null || IsIdentifier(outcome.AfterEvidenceId));

    private static bool IsIdentifier(string? value)
        => CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);

    private DateTimeOffset UtcNowOrThrow(DateTimeOffset minimum)
    {
        try
        {
            var utc = timeProvider.GetUtcNow();
            if (utc != default && utc.Offset == TimeSpan.Zero && utc >= minimum)
            {
                return utc;
            }
        }
        catch (Exception)
        {
            // The caller converts this fail-closed signal into durable evidence posture.
        }
        throw new GovernedLoopEffectAttemptEvidenceException("Trusted UTC time became unavailable.");
    }
}
