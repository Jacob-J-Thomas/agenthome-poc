using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Retry;
using EmbodySense.Core.Application.Loops.Retry.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Startup.Loops.Execution.Retry;

/// <summary>Projects fresh canonical lifecycle, authority, dependencies, and exact retry-series usage.</summary>
internal sealed class GovernedLoopCanonicalRetryCurrentPostureAdapter : IGovernedLoopRetryCurrentPosturePort
{
    private readonly ICustomLoopRunStore _runStore;
    private readonly IGovernedLoopSleepCurrentPosturePort _sleepPosture;
    private readonly ICapabilityAdmissionService _capabilityAdmission;

    internal GovernedLoopCanonicalRetryCurrentPostureAdapter(
        ICustomLoopRunStore runStore,
        IGovernedLoopSleepCurrentPosturePort sleepPosture,
        ICapabilityAdmissionService capabilityAdmission)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _sleepPosture = sleepPosture ?? throw new ArgumentNullException(nameof(sleepPosture));
        _capabilityAdmission = capabilityAdmission ?? throw new ArgumentNullException(nameof(capabilityAdmission));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopRetryCurrentPostureReadResult?> ReadAsync(
        CustomLoopRunRecord run,
        GovernedLoopRetryPolicy policy,
        GovernedLoopFailureEvidence failure,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CustomLoopRunValidator.Validate(run).IsValid
            || !GovernedLoopRetryContract.IsValid(policy)
            || !EmbodySense.Core.Common.Loops.Failures.GovernedLoopFailureEvidenceContract.IsValid(failure)
            || !string.Equals(policy.NodeId, failure.NodeId, StringComparison.Ordinal))
        {
            return Result(GovernedLoopRetryCurrentPostureReadStatus.Conflict);
        }

        CustomLoopRunRecord? current;
        try
        {
            current = await _runStore.GetAsync(run.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopRetryCurrentPostureReadStatus.Unavailable);
        }
        if (current is null
            || !CustomLoopRunValidator.Validate(current).IsValid
            || !CustomLoopRunValidator.HasExactDurableEventPrefix(run, current)
            || current.SequentialAdapterBinding is not { } binding
            || current.Events.Count(item => string.Equals(item.FailureEvidence?.ContentHash, failure.ContentHash, StringComparison.Ordinal)) != 1)
        {
            return Result(GovernedLoopRetryCurrentPostureReadStatus.Conflict);
        }

        GovernedLoopSleepCurrentPostureReadResult? sleep;
        CapabilityRevalidationResult capabilities;
        try
        {
            sleep = await _sleepPosture.ReadAsync(binding.ExecutionBinding, cancellationToken).ConfigureAwait(false);
            var allowed = LoopCapabilityRequirements.GetAssignedCapabilityIds(current.CapabilityAdmission.Requirements);
            capabilities = await _capabilityAdmission.RevalidateAsync(current.CapabilityAdmission, allowed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopRetryCurrentPostureReadStatus.Unavailable);
        }

        if (sleep is not { Status: GovernedLoopSleepCurrentPostureReadStatus.Found, Posture: { } sleepPosture })
        {
            return Result(sleep?.Status == GovernedLoopSleepCurrentPostureReadStatus.Conflict
                ? GovernedLoopRetryCurrentPostureReadStatus.Conflict
                : GovernedLoopRetryCurrentPostureReadStatus.Unavailable);
        }
        if (!IsExactPosture(current, binding.ExecutionBinding, sleepPosture))
        {
            return Result(GovernedLoopRetryCurrentPostureReadStatus.Conflict);
        }
        if (!TryProjectBudget(current, policy, failure, out var budget))
        {
            return Result(GovernedLoopRetryCurrentPostureReadStatus.Conflict);
        }

        return Result(
            GovernedLoopRetryCurrentPostureReadStatus.Found,
            new GovernedLoopRetryCurrentPosture(
                current.Status is CustomLoopRunStatus.Running or CustomLoopRunStatus.Waiting,
                sleepPosture.UnattendedExecutionPermitted,
                capabilities.IsValid,
                budget!,
                sleepPosture.ObservedAtUtc));
    }

    private static bool TryProjectBudget(
        CustomLoopRunRecord run,
        GovernedLoopRetryPolicy policy,
        GovernedLoopFailureEvidence failure,
        out GovernedLoopRetryBudgetSnapshot? budget)
    {
        budget = null;
        var attemptEvents = run.Events.Where(item => item.SequentialNodeEvidence is { } evidence
            && evidence.ActivationOrdinal == failure.ActivationOrdinal
            && evidence.VisitOrdinal == failure.VisitOrdinal).ToArray();
        var startEvents = attemptEvents
            .Where(item => item.SequentialNodeEvidence?.Kind == CustomLoopSequentialNodeEvidenceKind.DispatchStarted)
            .OrderBy(item => item.SequentialNodeEvidence!.Attempt)
            .ToArray();
        var starts = startEvents.Select(item => item.SequentialNodeEvidence!.Attempt).ToArray();
        if (starts.Length != failure.Attempt
            || !starts.SequenceEqual(Enumerable.Range(1, failure.Attempt).Select(value => (int?)value)))
        {
            return false;
        }

        if (!TrySelectAttemptEvidence(attemptEvents, failure.Attempt, out var modelEvidence))
        {
            return false;
        }
        var definitelyNotDispatched = failure.FailureClass is GovernedLoopFailureClass.DependencyUnavailableBeforeDispatch or GovernedLoopFailureClass.DispatchProvedNotStarted;
        var tokens = SumTokens(modelEvidence, definitelyNotDispatched);
        if (!TrySumCost(modelEvidence, definitelyNotDispatched, policy.MaximumCostCurrency, out var cost, out var currency))
        {
            return false;
        }
        if (!TryCountToolCalls(run.Events, startEvents, failure, out var toolCalls))
        {
            return false;
        }

        budget = new GovernedLoopRetryBudgetSnapshot(
            failure.Attempt,
            tokens,
            toolCalls,
            cost,
            currency,
            failure.Attempt);
        return true;
    }

    private static bool TryCountToolCalls(
        IReadOnlyList<CustomLoopRunEvent> events,
        IReadOnlyList<CustomLoopRunEvent> starts,
        GovernedLoopFailureEvidence failure,
        out int toolCalls)
    {
        toolCalls = 0;
        foreach (var start in starts)
        {
            var attempt = start.SequentialNodeEvidence!.Attempt!.Value;
            var terminal = events
                .Where(item => item.Sequence > start.Sequence
                    && item.FailureEvidence is { } evidence
                    && evidence.ActivationOrdinal == failure.ActivationOrdinal
                    && evidence.VisitOrdinal == failure.VisitOrdinal
                    && evidence.Attempt == attempt
                    && string.Equals(evidence.NodeId, failure.NodeId, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (terminal.Length != 1)
            {
                return false;
            }

            try
            {
                toolCalls = checked(toolCalls + events.Count(item => item.Sequence > start.Sequence
                    && item.Sequence < terminal[0].Sequence
                    && item.Kind == CustomLoopRunEventKind.ToolRequestReserved
                    && item.Attempt == attempt
                    && string.Equals(item.StepId, failure.NodeId, StringComparison.Ordinal)));
            }
            catch (OverflowException)
            {
                return false;
            }
            if (toolCalls > GovernedLoopRetryContractLimits.MaximumToolCalls)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExactPosture(
        CustomLoopRunRecord run,
        EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionBinding binding,
        GovernedLoopSleepCurrentPosture posture)
        => posture.Execution.Lifecycle.Payload.LifecycleVersion == run.LifecycleVersion
            && posture.Execution.Lifecycle.Payload.UpdatedAtUtc == run.UpdatedAtUtc
            && posture.Execution.Frontier.Payload.ContentHash == run.Frontier?.Payload.ContentHash
            && posture.ObservedAtUtc >= run.UpdatedAtUtc
            && SameBinding(posture.Execution.Lifecycle.Binding, binding)
            && SameBinding(posture.Execution.Frontier.Binding, binding);

    private static bool SameBinding(
        EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionBinding left,
        EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionBinding right)
        => left.SchemaVersion == right.SchemaVersion
            && left.ExecutionGeneration == right.ExecutionGeneration
            && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
            && string.Equals(left.Revision.GraphId, right.Revision.GraphId, StringComparison.Ordinal)
            && left.Revision.RevisionId == right.Revision.RevisionId
            && string.Equals(left.Revision.ExecutableHash, right.Revision.ExecutableHash, StringComparison.Ordinal);

    private static bool TrySelectAttemptEvidence(
        IReadOnlyList<CustomLoopRunEvent> attemptEvents,
        int attemptCount,
        out GovernedModelAttemptExecutionEvidence[] evidence)
    {
        var selected = new List<GovernedModelAttemptExecutionEvidence>(attemptCount);
        for (var attempt = 1; attempt <= attemptCount; attempt++)
        {
            var matches = attemptEvents
                .Where(item => item.SequentialNodeEvidence?.Attempt == attempt && item.ModelExecutionEvidence is not null)
                .Select(item => item.ModelExecutionEvidence!)
                .GroupBy(item => item.ContentHash, StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(2)
                .ToArray();
            if (matches.Length > 1)
            {
                evidence = [];
                return false;
            }
            if (matches.Length == 1)
            {
                selected.Add(matches[0]);
            }
        }

        if (selected.Select(item => item.ContentHash).Distinct(StringComparer.Ordinal).Count() != selected.Count)
        {
            evidence = [];
            return false;
        }

        evidence = [.. selected];
        return true;
    }

    private static long? SumTokens(IReadOnlyList<GovernedModelAttemptExecutionEvidence> evidence, bool definitelyNotDispatched)
    {
        if (evidence.Count == 0)
        {
            return definitelyNotDispatched ? 0 : null;
        }
        if (evidence.Any(item => item.Usage.TotalTokens.Status != GovernedModelUsageEvidenceStatus.Authoritative))
        {
            return null;
        }

        try
        {
            return evidence.Sum(item => item.Usage.TotalTokens.Value);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool TrySumCost(
        IReadOnlyList<GovernedModelAttemptExecutionEvidence> evidence,
        bool definitelyNotDispatched,
        string? policyCurrency,
        out long? cost,
        out string? currency)
    {
        cost = null;
        currency = null;
        if (evidence.Count == 0)
        {
            if (definitelyNotDispatched && policyCurrency is not null)
            {
                cost = 0;
                currency = policyCurrency;
            }
            return true;
        }
        if (evidence.Any(item => item.Usage.MonetaryCost.Status != GovernedModelUsageEvidenceStatus.Authoritative))
        {
            return true;
        }

        var currencies = evidence.Select(item => item.Usage.MonetaryCost.Currency).Distinct(StringComparer.Ordinal).ToArray();
        if (currencies.Length != 1 || currencies[0] is null)
        {
            return false;
        }
        try
        {
            cost = evidence.Sum(item => item.Usage.MonetaryCost.Micros);
            currency = currencies[0];
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static GovernedLoopRetryCurrentPostureReadResult Result(
        GovernedLoopRetryCurrentPostureReadStatus status,
        GovernedLoopRetryCurrentPosture? posture = null)
        => new(status, posture);
}
