using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Runs one serialized, bounded Startup recovery pass for every Human Review lane.</summary>
/// <remarks>
/// The adapter owns only in-memory cursors and a short candidate buffer. It scans the canonical run store for accepted
/// approvals whose continuation wake was not published, invokes the existing publication service, and then delegates
/// continuation and non-approval action recovery to their host-neutral Application coordinators. It owns no timer,
/// queue, durable cursor, secondary run store, or provider dispatch boundary.
/// </remarks>
public sealed class HumanReviewRecoveryRunner : IHumanReviewRecoveryRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IGovernedLoopLocalWorkRunner _inner;
    private readonly HumanReviewContinuationRecoveryCoordinator _continuationRecovery;
    private readonly HumanReviewDecisionActionRecoveryCoordinator _decisionActionRecovery;
    private readonly HumanReviewRecoveryReadinessSignal _readiness;
    private readonly IHumanReviewContinuationPublicationService _publication;
    private readonly ICustomLoopRunStore _runs;
    private readonly HumanReviewRecoveryRunnerOptions _options;
    private readonly Queue<string> _publicationCandidates = new();
    private readonly HashSet<string> _publicationQueuedRunIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publicationSeenRunIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publicationSeenAdmissionOperationIds = new(StringComparer.Ordinal);
    private string? _continuationScanCursor;
    private string? _decisionActionScanCursor;
    private string? _publicationScanCursor;
    private bool _publicationSourceTruncated;

    /// <summary>Initializes the serialized recovery adapter over the sole canonical run and Application boundaries.</summary>
    /// <param name="inner">The existing local runner that owns all non-Human Review work families.</param>
    /// <param name="runs">The canonical custom-loop run store used only for bounded approval publication discovery.</param>
    /// <param name="publication">The existing canonical approval continuation publication service.</param>
    /// <param name="continuationRecovery">The host-neutral approved-continuation recovery coordinator.</param>
    /// <param name="decisionActionRecovery">The host-neutral non-approval action recovery coordinator.</param>
    /// <param name="options">The bounded worker and scan configuration.</param>
    /// <param name="readiness">The Startup-owned readiness signal, or a new process-local signal when omitted.</param>
    internal HumanReviewRecoveryRunner(
        IGovernedLoopLocalWorkRunner inner,
        ICustomLoopRunStore runs,
        IHumanReviewContinuationPublicationService publication,
        HumanReviewContinuationRecoveryCoordinator continuationRecovery,
        HumanReviewDecisionActionRecoveryCoordinator decisionActionRecovery,
        HumanReviewRecoveryRunnerOptions options,
        HumanReviewRecoveryReadinessSignal? readiness = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _publication = publication ?? throw new ArgumentNullException(nameof(publication));
        _continuationRecovery = continuationRecovery ?? throw new ArgumentNullException(nameof(continuationRecovery));
        _decisionActionRecovery = decisionActionRecovery ?? throw new ArgumentNullException(nameof(decisionActionRecovery));
        _options = ValidateOptions(options);
        _readiness = readiness ?? new HumanReviewRecoveryReadinessSignal();
    }

    /// <inheritdoc />
    public bool IsExecutable => _readiness.IsExecutable;

    /// <inheritdoc />
    public async Task<GovernedLoopLocalWorkResult?> RunOnceAsync(GovernedLoopLocalWorkFamily family, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsHumanReviewFamily(family))
            {
                return await _inner.RunOnceAsync(family, cancellationToken).ConfigureAwait(false);
            }

            var execution = await RecoverUnderGateAsync(cancellationToken).ConfigureAwait(false);
            var result = Map(execution);
            await _readiness.ObserveAsync(result.Status, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<HumanReviewRecoveryPassResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var execution = await RecoverUnderGateAsync(cancellationToken).ConfigureAwait(false);
            await _readiness.ObserveAsync(execution.Status, cancellationToken).ConfigureAwait(false);
            return Project(execution);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopLocalWorkResult?> ProbeReadinessAsync(GovernedLoopLocalWorkFamily family, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsHumanReviewFamily(family))
            {
                return _inner is IGovernedLoopLocalWorkReadinessProbe probe
                    ? await probe.ProbeReadinessAsync(family, cancellationToken).ConfigureAwait(false)
                    : new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Unavailable, "work-readiness-probe-unavailable");
            }

            var result = await ProbeCanonicalRunsAsync(cancellationToken).ConfigureAwait(false);
            await _readiness.ObserveAsync(result.Status, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HumanReviewRecoveryExecutionResult> RecoverUnderGateAsync(CancellationToken cancellationToken)
    {
        var publication = await RecoverApprovalPublicationAsync(cancellationToken).ConfigureAwait(false);
        if (publication.Items.Any(item => item.Status == HumanReviewPublicationRecoveryItemStatus.Published))
        {
            // A newly committed wake can be ordered before the old continuation cursor. Re-scan from the
            // canonical beginning; an exact replay deliberately does not reset the cursor.
            _continuationScanCursor = null;
        }

        var continuation = await RecoverContinuationsAsync(cancellationToken).ConfigureAwait(false);
        var decisionAction = await RecoverDecisionActionsAsync(cancellationToken).ConfigureAwait(false);
        return new(MapStatus(publication.Status, continuation.Status, decisionAction.Status), publication, continuation, decisionAction);
    }

    private async Task<HumanReviewPublicationRecoveryResult> RecoverApprovalPublicationAsync(CancellationToken cancellationToken)
    {
        if (_publicationCandidates.Count == 0)
        {
            var page = await ReadApprovalPageAsync(cancellationToken).ConfigureAwait(false);
            if (page is not null)
            {
                return page;
            }
        }

        if (_publicationCandidates.Count == 0)
        {
            return new(HumanReviewPublicationRecoveryStatus.Current, _publicationScanCursor, _publicationSourceTruncated, []);
        }

        var runId = _publicationCandidates.Peek();
        HumanReviewContinuationStoreMutationResult? published;
        try
        {
            published = await _publication.PublishAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            RemovePublicationCandidate(runId);
            return new(HumanReviewPublicationRecoveryStatus.Unavailable, _publicationScanCursor, _publicationSourceTruncated, [new(runId, HumanReviewPublicationRecoveryItemStatus.Parked)]);
        }

        if (published is null || !Enum.IsDefined(published.Status))
        {
            RemovePublicationCandidate(runId);
            return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, _publicationSourceTruncated, [new(runId, HumanReviewPublicationRecoveryItemStatus.Invalid)]);
        }

        var itemStatus = published.Status switch
        {
            HumanReviewContinuationStoreMutationStatus.Committed => HumanReviewPublicationRecoveryItemStatus.Published,
            HumanReviewContinuationStoreMutationStatus.Replayed => HumanReviewPublicationRecoveryItemStatus.Replayed,
            HumanReviewContinuationStoreMutationStatus.Invalid => HumanReviewPublicationRecoveryItemStatus.Invalid,
            _ => HumanReviewPublicationRecoveryItemStatus.Parked,
        };

        if (published.Status is HumanReviewContinuationStoreMutationStatus.Committed or HumanReviewContinuationStoreMutationStatus.Replayed or HumanReviewContinuationStoreMutationStatus.Invalid)
        {
            RemovePublicationCandidate(runId);
        }
        else
        {
            // Defer response-unknown, contention, not-found, and bounded-capacity outcomes until the next bounded
            // canonical scan. Retaining them in the page buffer would starve later pages when every item is parked.
            RemovePublicationCandidate(runId);
        }

        var laneStatus = itemStatus == HumanReviewPublicationRecoveryItemStatus.Invalid
            ? HumanReviewPublicationRecoveryStatus.Invalid
            : published.Status == HumanReviewContinuationStoreMutationStatus.Unavailable
                ? HumanReviewPublicationRecoveryStatus.Unavailable
                : HumanReviewPublicationRecoveryStatus.Current;
        return new(laneStatus, _publicationScanCursor, _publicationSourceTruncated, [new(runId, itemStatus)]);
    }

    private void RemovePublicationCandidate(string runId)
    {
        if (_publicationCandidates.Count == 0)
        {
            _publicationQueuedRunIds.Remove(runId);
            return;
        }

        var candidate = _publicationCandidates.Dequeue();
        if (!string.Equals(candidate, runId, StringComparison.Ordinal))
        {
            _publicationCandidates.Enqueue(candidate);
            return;
        }

        _publicationQueuedRunIds.Remove(runId);
    }

    private async Task<HumanReviewPublicationRecoveryResult?> ReadApprovalPageAsync(CancellationToken cancellationToken)
    {
        CustomLoopRunPage? page;
        try
        {
            page = await _runs.ListPageAsync(new CustomLoopRunPageRequest(_options.MaximumCount, null, _publicationScanCursor), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, false, []);
        }
        catch (FormatException)
        {
            return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, false, []);
        }
        catch
        {
            return new(HumanReviewPublicationRecoveryStatus.Unavailable, _publicationScanCursor, false, []);
        }

        if (!IsValidPage(page, _options.MaximumCount) || page!.ContinuationCursor is not null && string.Equals(page.ContinuationCursor, _publicationScanCursor, StringComparison.Ordinal))
        {
            return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, false, []);
        }

        var startsNewScan = _publicationScanCursor is null;
        var nextScanCursor = page.ContinuationCursor;
        var sourceTruncated = nextScanCursor is not null;
        var seenRunIds = new HashSet<string>(StringComparer.Ordinal);
        var seenAdmissionOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var candidateIds = new List<string>();
        foreach (var summary in page.Items)
        {
            if (!IsValidSummary(summary)
                || !seenRunIds.Add(summary.Id)
                || (!startsNewScan && _publicationSeenRunIds.Contains(summary.Id))
                || !seenAdmissionOperationIds.Add(summary.AdmissionOperationId)
                || (!startsNewScan && _publicationSeenAdmissionOperationIds.Contains(summary.AdmissionOperationId)))
            {
                return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, _publicationSourceTruncated, []);
            }

            if (summary.IsDeleted)
            {
                continue;
            }

            CustomLoopRunRecord? run;
            try
            {
                run = await _runs.GetAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ArgumentException)
            {
                return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, _publicationSourceTruncated, []);
            }
            catch (FormatException)
            {
                return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, _publicationSourceTruncated, []);
            }
            catch
            {
                return new(HumanReviewPublicationRecoveryStatus.Unavailable, _publicationScanCursor, _publicationSourceTruncated, []);
            }

            bool matches;
            try
            {
                matches = run is not null && MatchesSummary(summary, run);
            }
            catch
            {
                matches = false;
            }

            if (run is null || !matches)
            {
                return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, _publicationSourceTruncated, []);
            }

            bool valid;
            try
            {
                valid = CustomLoopRunValidator.Validate(run).IsValid;
            }
            catch
            {
                return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, _publicationSourceTruncated, []);
            }

            if (!valid)
            {
                return new(HumanReviewPublicationRecoveryStatus.Invalid, _publicationScanCursor, _publicationSourceTruncated, []);
            }

            if (NeedsApprovalPublication(run) && !_publicationQueuedRunIds.Contains(run.Id))
            {
                candidateIds.Add(run.Id);
            }
        }

        foreach (var candidateId in candidateIds)
        {
            _publicationCandidates.Enqueue(candidateId);
            _publicationQueuedRunIds.Add(candidateId);
        }

        if (startsNewScan)
        {
            _publicationSeenRunIds.Clear();
            _publicationSeenAdmissionOperationIds.Clear();
        }

        _publicationSeenRunIds.UnionWith(seenRunIds);
        _publicationSeenAdmissionOperationIds.UnionWith(seenAdmissionOperationIds);

        // Commit the cursor only after every summary and every non-tombstone canonical reread has passed
        // validation. A malformed or unavailable page therefore remains retryable from the previous cursor.
        _publicationScanCursor = nextScanCursor;
        _publicationSourceTruncated = sourceTruncated;
        return null;
    }

    private async Task<HumanReviewContinuationRecoveryResult> RecoverContinuationsAsync(CancellationToken cancellationToken)
    {
        HumanReviewContinuationRecoveryResult result;
        try
        {
            result = await _continuationRecovery.RecoverAsync(new HumanReviewContinuationRecoveryRequest(
                _options.MaximumCount,
                _continuationScanCursor,
                _options.WorkerId,
                _options.CoordinatorSourceId,
                _options.ClaimLeaseDuration), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(HumanReviewContinuationRecoveryStatus.Unavailable, _continuationScanCursor, false, []);
        }

        if (result is null || !IsValidContinuationResult(result) || !IsValidContinuationCursorTransition(result.Status, result.NextScanCursor, _continuationScanCursor))
        {
            return new(HumanReviewContinuationRecoveryStatus.Invalid, _continuationScanCursor, false, result?.Items ?? []);
        }

        if (result.Status == HumanReviewContinuationRecoveryStatus.Current)
        {
            _continuationScanCursor = result.NextScanCursor;
        }

        return result;
    }

    private async Task<HumanReviewDecisionActionRecoveryResult> RecoverDecisionActionsAsync(CancellationToken cancellationToken)
    {
        HumanReviewDecisionActionRecoveryResult result;
        try
        {
            result = await _decisionActionRecovery.RecoverAsync(new HumanReviewDecisionActionRecoveryRequest(
                _options.MaximumCount,
                _decisionActionScanCursor,
                _options.WorkerId,
                _options.ClaimLeaseDuration), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(HumanReviewDecisionActionRecoveryStatus.Unavailable, _decisionActionScanCursor, false, []);
        }

        if (result is null || !IsValidDecisionActionResult(result) || !IsValidDecisionActionCursorTransition(result.Status, result.NextScanCursor, _decisionActionScanCursor))
        {
            return new HumanReviewDecisionActionRecoveryResult(HumanReviewDecisionActionRecoveryStatus.Invalid, _decisionActionScanCursor, false, [])
            {
                PublicationItems = result?.PublicationItems ?? []
            };
        }

        if (result.Status == HumanReviewDecisionActionRecoveryStatus.Current)
        {
            _decisionActionScanCursor = result.NextScanCursor;
        }

        return result;
    }

    private async Task<GovernedLoopLocalWorkResult> ProbeCanonicalRunsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var page = await _runs.ListPageAsync(new CustomLoopRunPageRequest(1), cancellationToken).ConfigureAwait(false);
            return IsValidPage(page, 1)
                ? new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "human-review-recovery-ready")
                : new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Corrupt, "human-review-recovery-page-corrupt");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Corrupt, "human-review-recovery-page-corrupt");
        }
        catch (FormatException)
        {
            return new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Corrupt, "human-review-recovery-page-corrupt");
        }
        catch
        {
            return new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Unavailable, "human-review-recovery-unavailable");
        }
    }

    private static bool IsValidPage(CustomLoopRunPage? page, int maximumCount)
        => page is not null
            && page.Items is not null
            && page.Items.Count <= maximumCount
            && IsValidCursor(page.ContinuationCursor)
            && page.Items.All(IsValidSummary);

    private static bool IsValidSummary(CustomLoopRunSummary? summary)
        => summary is not null
            && CustomLoopArtifactIdentifier.IsValid(summary.Id)
            && CustomLoopArtifactIdentifier.IsValid(summary.LoopId)
            && CustomLoopArtifactIdentifier.IsValid(summary.AdmissionOperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
            && Enum.IsDefined(summary.Status)
            && summary.DefinitionVersion >= 1
            && (summary.IsDeleted ? summary.LifecycleVersion == 0 && IsTerminal(summary.Status) : summary.LifecycleVersion >= 1)
            && summary.CreatedAtUtc >= DateTimeOffset.UnixEpoch
            && summary.UpdatedAtUtc >= summary.CreatedAtUtc
            && (summary.CompletedAtUtc is null || summary.CompletedAtUtc >= summary.CreatedAtUtc)
            && summary.Iteration >= 0
            && summary.NextStepIndex >= 0
            && (!summary.IsDeleted
                || summary.CompletedAtUtc is not null
                && summary.UpdatedAtUtc >= summary.CompletedAtUtc
                && summary.Iteration == 0
                && summary.NextStepIndex == 0
                && summary.FailureCode is null);

    private static bool MatchesSummary(CustomLoopRunSummary summary, CustomLoopRunRecord run)
        => string.Equals(summary.Id, run.Id, StringComparison.Ordinal)
            && string.Equals(summary.LoopId, run.LoopId, StringComparison.Ordinal)
            && string.Equals(summary.AdmissionOperationId, run.AdmissionOperationId, StringComparison.Ordinal)
            && summary.DefinitionVersion == run.AdmittedDefinition.DefinitionVersion
            && summary.LifecycleVersion == run.LifecycleVersion
            && summary.Status == run.Status
            && summary.CreatedAtUtc == run.CreatedAtUtc
            && summary.UpdatedAtUtc == run.UpdatedAtUtc
            && summary.CompletedAtUtc == run.CompletedAtUtc
            && summary.Iteration == run.Checkpoint.Iteration
            && summary.NextStepIndex == run.Checkpoint.NextStepIndex
            && string.Equals(summary.FailureCode, run.FailureCode, StringComparison.Ordinal);

    private static bool IsTerminal(CustomLoopRunStatus status)
        => status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;

    private static bool IsValidContinuationResult(HumanReviewContinuationRecoveryResult result)
        => Enum.IsDefined(result.Status)
            && result.Items is not null
            && result.Items.All(item => item is not null && Enum.IsDefined(item.Status));

    private static bool IsValidDecisionActionResult(HumanReviewDecisionActionRecoveryResult result)
        => Enum.IsDefined(result.Status)
            && result.Items is not null
            && result.PublicationItems is not null
            && result.Items.All(item => item is not null && Enum.IsDefined(item.Status))
            && result.PublicationItems.All(item => item is not null && Enum.IsDefined(item.Status));

    private static bool IsValidContinuationCursorTransition(HumanReviewContinuationRecoveryStatus status, string? nextCursor, string? currentCursor)
        => IsValidCursor(nextCursor)
            && (status != HumanReviewContinuationRecoveryStatus.Current
                || nextCursor is null
                || !string.Equals(nextCursor, currentCursor, StringComparison.Ordinal));

    private static bool IsValidDecisionActionCursorTransition(HumanReviewDecisionActionRecoveryStatus status, string? nextCursor, string? currentCursor)
        => IsValidCursor(nextCursor)
            && (status != HumanReviewDecisionActionRecoveryStatus.Current
                || nextCursor is null
                || !string.Equals(nextCursor, currentCursor, StringComparison.Ordinal));

    private static bool IsValidCursor(string? cursor)
        => cursor is null
            || cursor.Length is > 0 and <= CustomLoopLimits.MaxRunPageCursorCharacters
            && !string.IsNullOrWhiteSpace(cursor);

    private static bool NeedsApprovalPublication(CustomLoopRunRecord run)
        => run.Status == CustomLoopRunStatus.Paused
            && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked
            && run.HumanReview is { } review
            && review.AcceptedTerminalDecision?.Kind == HumanReviewDecisionKind.Approve
            && review.Continuation is null;

    private static bool IsHumanReviewFamily(GovernedLoopLocalWorkFamily family) => family == GovernedLoopLocalWorkFamily.HumanReview;

    private static HumanReviewRecoveryRunnerOptions ValidateOptions(HumanReviewRecoveryRunnerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumCount is < 1 or > CustomLoopLimits.MaxRecentRunsPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumCount));
        }

        if (!HumanReviewIdentifier.IsValid(options.WorkerId))
        {
            throw new ArgumentException("The worker identity is not a valid Human Review identifier.", nameof(options.WorkerId));
        }

        if (!HumanReviewIdentifier.IsValid(options.CoordinatorSourceId))
        {
            throw new ArgumentException("The coordinator source identity is not a valid Human Review identifier.", nameof(options.CoordinatorSourceId));
        }

        if (options.ClaimLeaseDuration <= TimeSpan.Zero || options.ClaimLeaseDuration > HumanReviewContractLimits.MaxContinuationClaimLease)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ClaimLeaseDuration));
        }

        return options;
    }

    private static HumanReviewRecoveryPassStatus MapStatus(HumanReviewPublicationRecoveryStatus publication, HumanReviewContinuationRecoveryStatus continuation, HumanReviewDecisionActionRecoveryStatus decisionAction)
        => !Enum.IsDefined(publication)
            || !Enum.IsDefined(continuation)
            || !Enum.IsDefined(decisionAction)
            || publication is HumanReviewPublicationRecoveryStatus.Unknown or HumanReviewPublicationRecoveryStatus.Invalid
            || continuation is HumanReviewContinuationRecoveryStatus.Unknown or HumanReviewContinuationRecoveryStatus.Invalid
            || decisionAction is HumanReviewDecisionActionRecoveryStatus.Unknown or HumanReviewDecisionActionRecoveryStatus.Invalid
            ? HumanReviewRecoveryPassStatus.Invalid
            : publication == HumanReviewPublicationRecoveryStatus.Unavailable || continuation == HumanReviewContinuationRecoveryStatus.Unavailable || decisionAction == HumanReviewDecisionActionRecoveryStatus.Unavailable
                ? HumanReviewRecoveryPassStatus.Unavailable
                : HumanReviewRecoveryPassStatus.Current;

    private static HumanReviewRecoveryPassResult Project(HumanReviewRecoveryExecutionResult execution)
        => new(
            execution.Status,
            execution.Publication,
            execution.Continuation.NextScanCursor,
            execution.DecisionAction.NextScanCursor);

    private static GovernedLoopLocalWorkResult Map(HumanReviewRecoveryExecutionResult pass)
    {
        if (!Enum.IsDefined(pass.Status)
            || pass.Publication is null
            || pass.Continuation is null
            || pass.DecisionAction is null
            || pass.Publication.Items is null
            || pass.Continuation.Items is null
            || pass.DecisionAction.Items is null
            || pass.DecisionAction.PublicationItems is null
            || pass.Publication.Items.Any(item => item is null || !Enum.IsDefined(item.Status))
            || pass.Continuation.Items.Any(item => item is null || !Enum.IsDefined(item.Status))
            || pass.DecisionAction.Items.Any(item => item is null || !Enum.IsDefined(item.Status))
            || pass.DecisionAction.PublicationItems.Any(item => item is null || !Enum.IsDefined(item.Status)))
        {
            return new(GovernedLoopLocalWorkResultStatus.Corrupt, "human-review-recovery-item-corrupt");
        }

        if (pass.Status == HumanReviewRecoveryPassStatus.Invalid)
        {
            return new(GovernedLoopLocalWorkResultStatus.Corrupt, "human-review-recovery-corrupt");
        }

        if (pass.Status == HumanReviewRecoveryPassStatus.Unavailable)
        {
            return new(GovernedLoopLocalWorkResultStatus.Unavailable, "human-review-recovery-unavailable");
        }

        if (pass.Publication.Items.Any(item => item.Status == HumanReviewPublicationRecoveryItemStatus.Invalid)
            || pass.Continuation.Items.Any(item => item.Status == HumanReviewContinuationRecoveryItemStatus.Invalid)
            || pass.DecisionAction.Items.Any(item => item.Status == HumanReviewDecisionActionRecoveryItemStatus.Invalid)
            || pass.DecisionAction.PublicationItems.Any(item => item.Status == HumanReviewDecisionActionPublicationRecoveryItemStatus.Invalid))
        {
            return new(GovernedLoopLocalWorkResultStatus.Corrupt, "human-review-recovery-item-corrupt");
        }

        if (pass.Continuation.Items.Any(item => item.Status is HumanReviewContinuationRecoveryItemStatus.ClaimConflict or HumanReviewContinuationRecoveryItemStatus.StaleAfterClaim)
            || pass.DecisionAction.Items.Any(item => item.Status is HumanReviewDecisionActionRecoveryItemStatus.ClaimConflict or HumanReviewDecisionActionRecoveryItemStatus.StaleAfterClaim))
        {
            return new(GovernedLoopLocalWorkResultStatus.Conflict, "human-review-recovery-claim-conflict");
        }

        if (pass.Publication.Items.Any(item => item.Status == HumanReviewPublicationRecoveryItemStatus.Parked)
            || pass.Continuation.Items.Any(item => item.Status is HumanReviewContinuationRecoveryItemStatus.Parked or HumanReviewContinuationRecoveryItemStatus.ClaimReplayed or HumanReviewContinuationRecoveryItemStatus.ExpiredWakeRetained)
            || pass.DecisionAction.Items.Any(item => item.Status == HumanReviewDecisionActionRecoveryItemStatus.Parked)
            || pass.DecisionAction.PublicationItems.Any(item => item.Status == HumanReviewDecisionActionPublicationRecoveryItemStatus.Parked))
        {
            return new(GovernedLoopLocalWorkResultStatus.AttentionRequired, "human-review-recovery-parked");
        }

        if (pass.Publication.Items.Any(item => item.Status is HumanReviewPublicationRecoveryItemStatus.Published or HumanReviewPublicationRecoveryItemStatus.Replayed)
            || pass.Continuation.Items.Any(item => item.Status is HumanReviewContinuationRecoveryItemStatus.Completed or HumanReviewContinuationRecoveryItemStatus.Retired)
            || pass.DecisionAction.Items.Any(item => item.Status is HumanReviewDecisionActionRecoveryItemStatus.Completed or HumanReviewDecisionActionRecoveryItemStatus.Retired)
            || pass.DecisionAction.PublicationItems.Any(item => item.Status is HumanReviewDecisionActionPublicationRecoveryItemStatus.Published or HumanReviewDecisionActionPublicationRecoveryItemStatus.Replayed))
        {
            return new(GovernedLoopLocalWorkResultStatus.Completed, "human-review-recovery-completed");
        }

        return new(GovernedLoopLocalWorkResultStatus.Empty, "human-review-recovery-empty");
    }
}
