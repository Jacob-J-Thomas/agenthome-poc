using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Application.HumanInput.Publication;
using EmbodySense.Core.Application.HumanInput.Publication.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Runs one bounded, restart-safe Human Input response-continuation recovery attempt through the canonical local coordinator.</summary>
/// <remarks>
/// This adapter owns no durable cursor, queue, lease, wake ledger, or worker lifetime. Its in-memory scan cursor and
/// detached page are intentionally discarded on process exit, causing a successor to restart from the recovery source's
/// clean tail-probed scan. Each selected candidate is submitted to the canonical continuation service exactly once per
/// one-shot call; that service remains responsible for durable attachment, generic wake ownership, ordered advancement,
/// idempotency, and retirement.
/// </remarks>
public sealed class HumanInputResponseContinuationWorkRunner : IGovernedLoopLocalWorkRunner
{
    private static readonly HumanInputPolicyReference _policySourceHealthProbeReference = new("human-input-source-health", "revision-one");
    private readonly IHumanInputResponseContinuationWakePort _continuation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IGovernedLoopLocalWorkRunner _inner;
    private readonly int _maximumScanCount;
    private readonly IHumanInputPolicySource _policySource;
    private readonly IHumanInputRequestPublicationService _publication;
    private readonly HumanInputContinuationReadinessSignal _readiness;
    private readonly IHumanInputResponseContinuationCandidateSource _source;
    private readonly TimeProvider _timeProvider;
    private Queue<HumanInputResponseContinuationCandidate> _page = new();
    private string? _scanCursor;

    /// <summary>Creates a Human Input recovery lane over the existing canonical worker, policy source, discovery source, and continuation boundary.</summary>
    /// <param name="inner">The existing canonical local work runner for Schedule, Trigger, and Wake families.</param>
    /// <param name="source">The canonical opaque-cursor Human Input continuation discovery source.</param>
    /// <param name="policySource">The canonical exact-revision Human Input policy source health-probed before every Human Input one-shot.</param>
    /// <param name="publication">The canonical checkpoint-to-request-ledger publication reconciler that runs before every continuation wake.</param>
    /// <param name="continuation">The canonical Human Input response continuation and generic wake bridge.</param>
    /// <param name="maximumScanCount">The bounded number of checkpoint ordinals examined by one source read.</param>
    /// <param name="timeProvider">The trusted UTC clock used for source observations.</param>
    /// <param name="readiness">The Startup-owned current executable posture signal observed by the graph catalog.</param>
    public HumanInputResponseContinuationWorkRunner(
        IGovernedLoopLocalWorkRunner inner,
        IHumanInputResponseContinuationCandidateSource source,
        IHumanInputPolicySource policySource,
        IHumanInputRequestPublicationService publication,
        IHumanInputResponseContinuationWakePort continuation,
        int maximumScanCount,
        TimeProvider? timeProvider = null,
        HumanInputContinuationReadinessSignal? readiness = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _policySource = policySource ?? throw new ArgumentNullException(nameof(policySource));
        _publication = publication ?? throw new ArgumentNullException(nameof(publication));
        _continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));
        _maximumScanCount = maximumScanCount is < 1 or > CustomLoopLimits.MaxRecentRunsPageSize
            ? throw new ArgumentOutOfRangeException(nameof(maximumScanCount))
            : maximumScanCount;
        _readiness = readiness ?? new HumanInputContinuationReadinessSignal();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets whether the current Startup-composed worker has completed a clean bounded Human Input and policy-source probe.</summary>
    /// <remarks>Corrupt or unavailable dependency evidence clears this posture. Caller cancellation does not change it.</remarks>
    public bool IsExecutable => _readiness.IsExecutable;

    /// <inheritdoc />
    public async Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default)
    {
        if (family != GovernedLoopLocalWorkFamily.HumanInput)
        {
            return await _inner.RunOnceAsync(family, cancellationToken).ConfigureAwait(false);
        }

        return await RunHumanInputAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopLocalWorkResult?> RunHumanInputAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var publicationHealth = await ProbePublicationAsync(cancellationToken).ConfigureAwait(false);
            if (publicationHealth is not null)
            {
                _readiness.Observe(publicationHealth);
                return publicationHealth;
            }

            var policyHealth = await ProbePolicySourceAsync(cancellationToken).ConfigureAwait(false);
            if (policyHealth is not null)
            {
                _readiness.Observe(policyHealth);
                return policyHealth;
            }

            if (!TryGetUtcNow(out var observedAtUtc, out var clockFailure))
            {
                _readiness.Observe(clockFailure);
                return clockFailure;
            }

            var result = await RunHumanInputUnderGateAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);
            _readiness.Observe(result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GovernedLoopLocalWorkResult?> ProbePublicationAsync(CancellationToken cancellationToken)
    {
        HumanInputRequestPublicationHealthResult? result;
        try
        {
            result = await _publication.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-request-publication-health-unavailable");
        }

        if (result is null || !Enum.IsDefined(result.Status) || result.Status == HumanInputRequestPublicationHealthStatus.Unknown)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-request-publication-health-corrupt");
        }

        return result.Status switch
        {
            HumanInputRequestPublicationHealthStatus.Ready => null,
            HumanInputRequestPublicationHealthStatus.Unavailable => Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-request-publication-health-unavailable"),
            _ => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-request-publication-health-corrupt"),
        };
    }

    private async Task<GovernedLoopLocalWorkResult?> RunHumanInputUnderGateAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_page.Count == 0)
        {
            var read = await ReadPageAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);
            if (read is not null)
            {
                return read;
            }

            if (_page.Count == 0)
            {
                return Result(GovernedLoopLocalWorkResultStatus.Empty, "human-input-candidates-empty");
            }
        }

        var candidate = _page.Peek();
        HumanInputRequestPublicationResult? publication;
        try
        {
            publication = await _publication.PublishAsync(
                new HumanInputRequestPublicationRequest(candidate.RunId, candidate.CheckpointId, candidate.CheckpointHash),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-request-publication-unavailable");
        }

        if (publication is null || !Enum.IsDefined(publication.Status))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-request-publication-corrupt");
        }

        switch (publication.Status)
        {
            case HumanInputRequestPublicationStatus.Published:
            case HumanInputRequestPublicationStatus.Replayed:
                break;
            case HumanInputRequestPublicationStatus.Stale:
                return EmptyPublication();
            case HumanInputRequestPublicationStatus.Unavailable:
                _page.Enqueue(_page.Dequeue());
                return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-request-publication-unavailable");
            default:
                return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-request-publication-corrupt");
        }

        HumanInputResponseContinuationWakeResult? wake;
        try
        {
            wake = await _continuation.WakeAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-continuation-unavailable");
        }

        if (wake is null || !Enum.IsDefined(wake.Status))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-continuation-result-corrupt");
        }

        return wake.Status switch
        {
            // The continuation service returns these only after the generic wake evidence confirms its continuation
            // disposition. Submitted and replayed therefore include durable terminal checkpoint/frontier and ordered
            // advancement evidence; Retired likewise includes its durable no-response convergence.
            HumanInputResponseContinuationWakeStatus.Submitted
                or HumanInputResponseContinuationWakeStatus.Replayed
                or HumanInputResponseContinuationWakeStatus.Retired
                => Complete(wake.Status),
            HumanInputResponseContinuationWakeStatus.Stale
                or HumanInputResponseContinuationWakeStatus.NoWork
                => Empty(wake.Status),
            HumanInputResponseContinuationWakeStatus.Unavailable
                => Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-continuation-unavailable"),
            HumanInputResponseContinuationWakeStatus.Invalid
                => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-continuation-invalid"),
            _ => Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-continuation-result-corrupt")
        };
    }

    private async Task<GovernedLoopLocalWorkResult?> ProbePolicySourceAsync(CancellationToken cancellationToken)
    {
        HumanInputPolicySourceReadResult? result;
        try
        {
            result = await _policySource.ReadAsync(_policySourceHealthProbeReference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-policy-source-unavailable");
        }

        if (result is null || !Enum.IsDefined(result.Status) || result.Status == HumanInputPolicySourceReadStatus.Unknown)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-policy-source-corrupt");
        }

        if (result.Status == HumanInputPolicySourceReadStatus.Unavailable)
        {
            return result.Policy is null && result.StoreGeneration == 0
                ? Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-policy-source-unavailable")
                : Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-policy-source-corrupt");
        }

        if (result.Status == HumanInputPolicySourceReadStatus.NotFound)
        {
            return result.Policy is null && result.StoreGeneration >= 0
                ? null
                : Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-policy-source-corrupt");
        }

        return result.Status == HumanInputPolicySourceReadStatus.Ready
            && result.StoreGeneration > 0
            && result.Policy is not null
            && Equals(result.Policy.Reference, _policySourceHealthProbeReference)
            && HumanInputPolicyArtifactValidator.Validate(result.Policy).IsValid
            ? null
            : Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-policy-source-corrupt");
    }

    private async Task<GovernedLoopLocalWorkResult?> ReadPageAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        HumanInputResponseContinuationRecoveryPage? page;
        try
        {
            page = await _source.ListCandidatesAsync(_maximumScanCount, _scanCursor, observedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-recovery-unavailable");
        }

        if (page?.Status == HumanInputResponseContinuationRecoveryPageStatus.Unavailable)
        {
            return Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-recovery-unavailable");
        }

        if (!IsValidPage(page))
        {
            return Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-recovery-page-corrupt");
        }

        _scanCursor = page!.NextScanCursor;
        foreach (var candidate in page.Candidates)
        {
            _page.Enqueue(candidate);
        }

        return null;
    }

    private bool TryGetUtcNow(out DateTimeOffset observedAtUtc, out GovernedLoopLocalWorkResult? failure)
    {
        try
        {
            observedAtUtc = _timeProvider.GetUtcNow();
        }
        catch
        {
            observedAtUtc = default;
            failure = Result(GovernedLoopLocalWorkResultStatus.Unavailable, "human-input-recovery-clock-unavailable");
            return false;
        }

        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            failure = Result(GovernedLoopLocalWorkResultStatus.Corrupt, "human-input-recovery-clock-corrupt");
            return false;
        }

        failure = null;
        return true;
    }

    private bool IsValidPage(HumanInputResponseContinuationRecoveryPage? page)
    {
        if (page is null
            || page.Status != HumanInputResponseContinuationRecoveryPageStatus.Current
            || page.Candidates is null
            || page.Candidates.Count > _maximumScanCount
            || page.Candidates.Count > 0 && page.NextScanCursor is null
            || page.HasMoreScanWork != (page.NextScanCursor is not null)
            || page.NextScanCursor is { Length: 0 })
        {
            return false;
        }

        return page.Candidates.All(IsValidCandidate)
            && page.Candidates.Select(candidate => string.Join('\n', candidate.RunId, candidate.CheckpointId)).Distinct(StringComparer.Ordinal).Count() == page.Candidates.Count;
    }

    private static bool IsValidCandidate(HumanInputResponseContinuationCandidate? candidate)
        => candidate is not null
            && CustomLoopArtifactIdentifier.IsValid(candidate.RunId)
            && HumanInputIdentifier.IsValid(candidate.CheckpointId)
            && IsSha256(candidate.CheckpointHash);

    private static GovernedLoopLocalWorkResult Result(GovernedLoopLocalWorkResultStatus status, string reason)
        => new(status, reason);

    private GovernedLoopLocalWorkResult Complete(HumanInputResponseContinuationWakeStatus status)
    {
        _ = _page.Dequeue();
        return Result(GovernedLoopLocalWorkResultStatus.Completed, WakeReason(status));
    }

    private GovernedLoopLocalWorkResult Empty(HumanInputResponseContinuationWakeStatus status)
    {
        _ = _page.Dequeue();
        return Result(GovernedLoopLocalWorkResultStatus.Empty, WakeReason(status));
    }

    private GovernedLoopLocalWorkResult EmptyPublication()
    {
        _ = _page.Dequeue();
        return Result(GovernedLoopLocalWorkResultStatus.Empty, "human-input-request-publication-stale");
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string WakeReason(HumanInputResponseContinuationWakeStatus status)
        => status switch
        {
            HumanInputResponseContinuationWakeStatus.Submitted => "human-input-continuation-submitted",
            HumanInputResponseContinuationWakeStatus.Replayed => "human-input-continuation-replayed",
            HumanInputResponseContinuationWakeStatus.Retired => "human-input-continuation-retired",
            HumanInputResponseContinuationWakeStatus.Stale => "human-input-continuation-stale",
            HumanInputResponseContinuationWakeStatus.NoWork => "human-input-continuation-no-work",
            _ => "human-input-continuation-unknown"
        };
}
