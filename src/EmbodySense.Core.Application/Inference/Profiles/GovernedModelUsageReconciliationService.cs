using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Owns the append-only dispatch/usage/reconciliation state machine after a durable pre-transport reservation.</summary>
public sealed class GovernedModelUsageReconciliationService
{
    private readonly IGovernedModelUsageLedger _ledger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the usage reconciliation service.</summary>
    public GovernedModelUsageReconciliationService(IGovernedModelUsageLedger ledger, TimeProvider? timeProvider = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Retains either affirmative dispatch-not-started proof or the irreversible provider boundary.</summary>
    public async Task<GovernedModelUsageTransitionResult> RecordDispatchAsync(GovernedModelDispatchEvidenceRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null || !GovernedModelContractValidator.IsValid(request.Identity) || !ModelProfileCatalogService.IsHash(request.ReservationEntryHash) || !ModelProfileCatalogService.IsHash(request.DispatchEvidenceHash))
        {
            return Result(GovernedModelUsageTransitionStatus.Invalid);
        }

        return await TransitionAsync(
            request.Identity,
            history =>
            {
                var reservation = history[0];
                if (history.Count != 1 || !string.Equals(reservation.ContentHash, request.ReservationEntryHash, StringComparison.Ordinal))
                {
                    return null;
                }
                return GovernedModelUsageLedgerEntry.Create(
                    1,
                    request.Identity,
                    history.Count + 1,
                    request.DispatchStarted ? GovernedModelUsageLedgerPhase.DispatchBoundaryReached : GovernedModelUsageLedgerPhase.DispatchProvedNotStarted,
                    reservation.Reservation,
                    null,
                    null,
                    null,
                    request.DispatchStarted,
                    request.DispatchEvidenceHash,
                    history[^1].ContentHash,
                    _timeProvider.GetUtcNow());
            },
            requestedPhase: request.DispatchStarted ? GovernedModelUsageLedgerPhase.DispatchBoundaryReached : GovernedModelUsageLedgerPhase.DispatchProvedNotStarted,
            replayMatches: entry => string.Equals(entry.EvidenceHash, request.DispatchEvidenceHash, StringComparison.Ordinal),
            conflictFactory: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retains exact provider usage or explicit unavailable posture once after dispatch.</summary>
    public async Task<GovernedModelUsageTransitionResult> ObserveUsageAsync(GovernedModelUsageObservationRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null || !GovernedModelContractValidator.IsValid(request.Identity) || !GovernedModelContractValidator.IsValid(request.Usage) || !ModelProfileCatalogService.IsHash(request.ReservationEntryHash) || !ModelProfileCatalogService.IsHash(request.ProviderEvidenceHash))
        {
            return Result(GovernedModelUsageTransitionStatus.Invalid);
        }

        return await TransitionAsync(
            request.Identity,
            history =>
            {
                var reservation = history[0];
                if (!string.Equals(reservation.ContentHash, request.ReservationEntryHash, StringComparison.Ordinal) || history[^1].Phase != GovernedModelUsageLedgerPhase.DispatchBoundaryReached)
                {
                    return null;
                }
                return GovernedModelUsageLedgerEntry.Create(
                    1,
                    request.Identity,
                    history.Count + 1,
                    GovernedModelUsageLedgerPhase.UsageObserved,
                    reservation.Reservation,
                    request.Usage,
                    null,
                    null,
                    HasUnknown(request.Usage, reservation.Reservation!),
                    request.ProviderEvidenceHash,
                    history[^1].ContentHash,
                    _timeProvider.GetUtcNow());
            },
            GovernedModelUsageLedgerPhase.UsageObserved,
            replayMatches: entry => string.Equals(entry.Usage?.ContentHash, request.Usage.ContentHash, StringComparison.Ordinal)
                && string.Equals(entry.EvidenceHash, request.ProviderEvidenceHash, StringComparison.Ordinal)
                || entry.Phase == GovernedModelUsageLedgerPhase.AttentionRequired
                    && string.Equals(entry.EvidenceHash, request.ProviderEvidenceHash, StringComparison.Ordinal),
            conflictFactory: history => GovernedModelUsageLedgerEntry.Create(
                1,
                request.Identity,
                history.Count + 1,
                GovernedModelUsageLedgerPhase.AttentionRequired,
                history[0].Reservation,
                history[^1].Usage,
                null,
                null,
                true,
                request.ProviderEvidenceHash,
                history[^1].ContentHash,
                _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconciles authoritative dimensions once; unknown dimensions remain conservatively reserved.</summary>
    public async Task<GovernedModelUsageTransitionResult> ReconcileAsync(GovernedModelUsageLedgerIdentity? identity, CancellationToken cancellationToken = default)
    {
        if (!GovernedModelContractValidator.IsValid(identity))
        {
            return Result(GovernedModelUsageTransitionStatus.Invalid);
        }

        return await TransitionAsync(
            identity!,
            history =>
            {
                if (history[^1].Phase != GovernedModelUsageLedgerPhase.UsageObserved || history[^1].Usage is null || history[0].Reservation is null)
                {
                    return null;
                }
                var usage = history[^1].Usage!;
                var reservation = history[0].Reservation!;
                var used = AuthoritativeVector(usage);
                var over = Exceeds(used, reservation, usage);
                var unknown = HasUnknown(usage, reservation);
                var phase = over ? GovernedModelUsageLedgerPhase.AttentionRequired : GovernedModelUsageLedgerPhase.Reconciled;
                var released = ReleaseVector(reservation, used, usage);
                return GovernedModelUsageLedgerEntry.Create(
                    1,
                    identity!,
                    history.Count + 1,
                    phase,
                    reservation,
                    usage,
                    used,
                    released,
                    unknown || over,
                    history[^1].ContentHash,
                    history[^1].ContentHash,
                    _timeProvider.GetUtcNow());
            },
            GovernedModelUsageLedgerPhase.Reconciled,
            replayMatches: _ => true,
            conflictFactory: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Durably retains unknown usage attention after any post-boundary exception, timeout, cancellation, owner loss, or malformed result.</summary>
    public async Task<GovernedModelUsageTransitionResult> RequireAttentionAsync(GovernedModelAmbiguousUsageRequest? request, CancellationToken cancellationToken = default)
    {
        if (request is null || !GovernedModelContractValidator.IsValid(request.Identity) || !ModelProfileCatalogService.IsHash(request.ReservationEntryHash) || !ModelProfileCatalogService.IsHash(request.AmbiguityEvidenceHash))
        {
            return Result(GovernedModelUsageTransitionStatus.Invalid);
        }

        return await TransitionAsync(
            request.Identity,
            history =>
            {
                var reservation = history[0];
                var current = history[^1];
                if (!string.Equals(reservation.ContentHash, request.ReservationEntryHash, StringComparison.Ordinal)
                    || current.Phase is not GovernedModelUsageLedgerPhase.DispatchBoundaryReached
                        and not GovernedModelUsageLedgerPhase.DispatchProvedNotStarted
                        and not GovernedModelUsageLedgerPhase.UsageObserved
                        and not GovernedModelUsageLedgerPhase.Reconciled)
                {
                    return null;
                }
                return GovernedModelUsageLedgerEntry.Create(
                    1,
                    request.Identity,
                    history.Count + 1,
                    GovernedModelUsageLedgerPhase.AttentionRequired,
                    reservation.Reservation,
                    current.Usage,
                    current.Used,
                    current.Released,
                    current.Phase switch
                    {
                        GovernedModelUsageLedgerPhase.DispatchProvedNotStarted => false,
                        GovernedModelUsageLedgerPhase.Reconciled => current.UsageUnknown,
                        _ => true
                    },
                    request.AmbiguityEvidenceHash,
                    history[^1].ContentHash,
                    _timeProvider.GetUtcNow());
            },
            GovernedModelUsageLedgerPhase.AttentionRequired,
            replayMatches: entry => string.Equals(entry.EvidenceHash, request.AmbiguityEvidenceHash, StringComparison.Ordinal),
            conflictFactory: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedModelUsageTransitionResult> TransitionAsync(
        GovernedModelUsageLedgerIdentity identity,
        Func<IReadOnlyList<GovernedModelUsageLedgerEntry>, GovernedModelUsageLedgerEntry?> create,
        GovernedModelUsageLedgerPhase requestedPhase,
        Func<GovernedModelUsageLedgerEntry, bool> replayMatches,
        Func<IReadOnlyList<GovernedModelUsageLedgerEntry>, GovernedModelUsageLedgerEntry>? conflictFactory,
        CancellationToken cancellationToken)
    {
        GovernedModelUsageLedgerReadResult? read;
        try
        {
            read = await _ledger.ReadAsync(identity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedModelUsageTransitionStatus.Unavailable);
        }
        if (!ModelUsageLedgerReadAuthentication.TryAuthenticate(read, identity, out var authenticated))
        {
            return Result(GovernedModelUsageTransitionStatus.Unavailable);
        }
        read = authenticated;
        var history = authenticated!.Entries;
        if (history.Count == 0)
        {
            return Result(GovernedModelUsageTransitionStatus.Conflict);
        }

        var replayCandidates = history.Where(entry => requestedPhase switch
        {
            GovernedModelUsageLedgerPhase.Reconciled => entry.Phase is GovernedModelUsageLedgerPhase.Reconciled or GovernedModelUsageLedgerPhase.AttentionRequired,
            GovernedModelUsageLedgerPhase.UsageObserved => entry.Phase is GovernedModelUsageLedgerPhase.UsageObserved or GovernedModelUsageLedgerPhase.AttentionRequired,
            _ => entry.Phase == requestedPhase
        }).ToArray();
        var matchingReplay = replayCandidates.FirstOrDefault(replayMatches);
        if (matchingReplay is not null)
        {
            return new GovernedModelUsageTransitionResult(matchingReplay.Phase == GovernedModelUsageLedgerPhase.AttentionRequired ? GovernedModelUsageTransitionStatus.AttentionRequired : GovernedModelUsageTransitionStatus.Replayed, matchingReplay);
        }
        var replay = replayCandidates.FirstOrDefault();
        if (replay is not null)
        {
            if (conflictFactory is null)
            {
                return Result(GovernedModelUsageTransitionStatus.Conflict);
            }
        }

        GovernedModelUsageLedgerEntry? next;
        try
        {
            next = replay is null ? create(history) : conflictFactory!(history);
        }
        catch
        {
            return Result(GovernedModelUsageTransitionStatus.Invalid);
        }
        if (next is null)
        {
            return Result(GovernedModelUsageTransitionStatus.Conflict);
        }
        if (!GovernedModelUsageLedgerHistoryValidator.IsValid(history.Concat([next]).ToArray(), identity, history.Count + 1))
        {
            return Result(GovernedModelUsageTransitionStatus.Conflict);
        }

        try
        {
            var append = await _ledger.AppendAsync(next, read!.Generation, cancellationToken).ConfigureAwait(false);
            if (append is null || !Enum.IsDefined(append.Status) || append.Status == 0)
            {
                return Result(GovernedModelUsageTransitionStatus.Unavailable);
            }
            if (append.Status is GovernedModelUsageLedgerAppendStatus.Conflict or GovernedModelUsageLedgerAppendStatus.BudgetExhausted)
            {
                return Result(GovernedModelUsageTransitionStatus.Conflict);
            }
            if (append.Status == GovernedModelUsageLedgerAppendStatus.Unavailable)
            {
                return Result(GovernedModelUsageTransitionStatus.Unavailable);
            }
            return await AuthenticateAsync(
                identity,
                next,
                append.Status == GovernedModelUsageLedgerAppendStatus.Appended ? GovernedModelUsageTransitionStatus.Applied : GovernedModelUsageTransitionStatus.Replayed).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AuthenticateAsync(identity, next, GovernedModelUsageTransitionStatus.Applied).ConfigureAwait(false);
        }
        catch
        {
            return await AuthenticateAsync(identity, next, GovernedModelUsageTransitionStatus.Applied).ConfigureAwait(false);
        }
    }

    private async Task<GovernedModelUsageTransitionResult> AuthenticateAsync(GovernedModelUsageLedgerIdentity identity, GovernedModelUsageLedgerEntry expected, GovernedModelUsageTransitionStatus exactSuccessStatus)
    {
        try
        {
            var retained = await _ledger.ReadAsync(identity, CancellationToken.None).ConfigureAwait(false);
            if (!ModelUsageLedgerReadAuthentication.TryAuthenticate(retained, identity, out var authenticated))
            {
                return Result(GovernedModelUsageTransitionStatus.Unavailable);
            }
            var exact = authenticated!.Entries.FirstOrDefault(entry => entry.Generation == expected.Generation);
            if (exact is null || !string.Equals(exact.ContentHash, expected.ContentHash, StringComparison.Ordinal))
            {
                return Result(GovernedModelUsageTransitionStatus.Conflict);
            }
            return new GovernedModelUsageTransitionResult(exact.Phase == GovernedModelUsageLedgerPhase.AttentionRequired ? GovernedModelUsageTransitionStatus.AttentionRequired : exactSuccessStatus, exact);
        }
        catch
        {
            return Result(GovernedModelUsageTransitionStatus.Unavailable);
        }
    }

    private static GovernedModelUsageVector AuthoritativeVector(LlmInferenceUsageEvidence usage)
        => GovernedModelUsageVector.Create(
            Value(usage.InputTokens),
            Value(usage.OutputTokens),
            Value(usage.CachedTokens),
            Value(usage.TotalTokens),
            usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative ? usage.MonetaryCost.Currency : null,
            usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative ? usage.MonetaryCost.Micros : 0);

    private static GovernedModelUsageVector ReleaseVector(GovernedModelUsageCeiling reservation, GovernedModelUsageVector used, LlmInferenceUsageEvidence usage)
        => GovernedModelUsageVector.Create(
            Release(reservation.InputTokens, used.InputTokens, usage.InputTokens.Status),
            Release(reservation.OutputTokens, used.OutputTokens, usage.OutputTokens.Status),
            Release(reservation.CachedTokens, used.CachedTokens, usage.CachedTokens.Status),
            Release(reservation.TotalTokens, used.TotalTokens, usage.TotalTokens.Status),
            reservation.MonetaryCost.Currency,
            reservation.MonetaryCost.IsBounded && usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative ? Math.Max(0, reservation.MonetaryCost.MaximumMicros - used.CostMicros) : 0);

    private static long Value(GovernedModelUsageMeasurement measurement) => measurement.Status == GovernedModelUsageEvidenceStatus.Authoritative ? measurement.Value : 0;
    private static long Release(GovernedModelUsageLimit reserved, long used, GovernedModelUsageEvidenceStatus status) => reserved.IsBounded && status == GovernedModelUsageEvidenceStatus.Authoritative ? Math.Max(0, reserved.Maximum - used) : 0;
    private static bool HasUnknown(LlmInferenceUsageEvidence usage, GovernedModelUsageCeiling reservation)
        => reservation.InputTokens.IsBounded && usage.InputTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.OutputTokens.IsBounded && usage.OutputTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.CachedTokens.IsBounded && usage.CachedTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.TotalTokens.IsBounded && usage.TotalTokens.Status == GovernedModelUsageEvidenceStatus.Unavailable
            || reservation.MonetaryCost.IsBounded && usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Unavailable;
    private static bool Exceeds(GovernedModelUsageVector used, GovernedModelUsageCeiling reservation, LlmInferenceUsageEvidence usage)
        => reservation.InputTokens.IsBounded && used.InputTokens > reservation.InputTokens.Maximum
            || reservation.OutputTokens.IsBounded && used.OutputTokens > reservation.OutputTokens.Maximum
            || reservation.CachedTokens.IsBounded && used.CachedTokens > reservation.CachedTokens.Maximum
            || reservation.TotalTokens.IsBounded && used.TotalTokens > reservation.TotalTokens.Maximum
            || reservation.MonetaryCost.IsBounded
                && usage.MonetaryCost.Status == GovernedModelUsageEvidenceStatus.Authoritative
                && (used.CostMicros > reservation.MonetaryCost.MaximumMicros
                || !string.Equals(used.Currency, reservation.MonetaryCost.Currency, StringComparison.Ordinal));
    private static GovernedModelUsageTransitionResult Result(GovernedModelUsageTransitionStatus status) => new(status, null);
}
