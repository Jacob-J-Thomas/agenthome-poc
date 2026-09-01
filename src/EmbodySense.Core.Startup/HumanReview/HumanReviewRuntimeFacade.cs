using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using CommonDecisionKind = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionKind;
using CommonDecision = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecision;
using CommonDecisionOperationDisposition = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionOperationDisposition;
using CommonDecisionOperationReceipt = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionOperationReceipt;
using CommonEffectCertainty = EmbodySense.Core.Common.HumanReview.Models.HumanReviewEffectCertainty;
using CommonEvidence = EmbodySense.Core.Common.HumanReview.Models.HumanReviewEvidence;
using CommonEvidenceKind = EmbodySense.Core.Common.HumanReview.Models.HumanReviewEvidenceKind;
using CommonFrontierStatus = EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopFrontierStatus;
using CommonLifecycleStatus = EmbodySense.Core.Common.HumanReview.Models.HumanReviewLifecycleStatus;
using CommonRedactedPreview = EmbodySense.Core.Common.HumanReview.Models.HumanReviewRedactedPreview;
using CommonPreviewKind = EmbodySense.Core.Common.HumanReview.Models.HumanReviewPreviewKind;
using CommonPurpose = EmbodySense.Core.Common.HumanReview.Models.HumanReviewPurpose;
using CommonRunState = EmbodySense.Core.Common.Loops.Models.Custom.Execution.HumanReviewRunState;
using CommonRunPage = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunPage;
using CommonRunPageRequest = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunPageRequest;
using CommonRunStatus = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunStatus;
using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Core.Startup.HumanReview;

/// <summary>Exposes one bounded surface-neutral projection over the canonical Human Review run state.</summary>
/// <remarks>All reads are detached and redact workspace, binding, actor, role, grant, connection, and raw effect values.
/// Decisions accept only the run, optimistic version, operation identity, closed decision, and bounded detail supplied by a
/// caller; authentication and every authority fact are resolved by the server-owned Application composition.</remarks>
public sealed class HumanReviewRuntimeFacade
{
    /// <summary>The maximum number of run summaries inspected by one page.</summary>
    public const int MaxPageSize = CustomLoopLimits.MaxRecentRunsPageSize;

    private readonly ICustomLoopRunStore _runs;
    private readonly IHumanReviewDecisionService _decisions;
    private readonly IHumanReviewCurrentEffectAttemptEvidenceSource? _effectEvidence;
    private readonly IGovernedLoopEffectCertaintySnapshotSource? _effectCertainty;

    /// <summary>Initializes one facade over the supplied canonical run and decision services.</summary>
    /// <param name="runs">The sole canonical durable custom-loop run store.</param>
    /// <param name="decisions">The canonical server-authorized Human Review decision service.</param>
    /// <param name="effectEvidence">The shared read-only canonical effect evidence source, when composed.</param>
    /// <param name="effectCertainty">The shared read-only canonical effect certainty source, when composed.</param>
    /// <exception cref="ArgumentNullException">Thrown when the run store or decision service is null.</exception>
    internal HumanReviewRuntimeFacade(
        ICustomLoopRunStore runs,
        IHumanReviewDecisionService decisions,
        IHumanReviewCurrentEffectAttemptEvidenceSource? effectEvidence = null,
        IGovernedLoopEffectCertaintySnapshotSource? effectCertainty = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _effectEvidence = effectEvidence;
        _effectCertainty = effectCertainty;
    }

    /// <summary>Reads the bounded first page of Human Review summaries.</summary>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>A detached page containing only canonical Human Review summaries.</returns>
    public Task<HumanReviewPage> ListAsync(CancellationToken cancellationToken = default)
        => ListAsync(new HumanReviewPageRequest(), cancellationToken);

    /// <summary>Reads one bounded page of Human Review summaries.</summary>
    /// <param name="request">The finite page bound and opaque canonical cursor.</param>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>A detached page; unsupported or malformed canonical state is fail-closed.</returns>
    public async Task<HumanReviewPage> ListAsync(HumanReviewPageRequest? request, CancellationToken cancellationToken = default)
    {
        if (!IsValidPageRequest(request))
        {
            return Page(HumanReviewPageStatus.Invalid);
        }

        CommonRunPage page;
        try
        {
            page = await _runs.ListPageAsync(new CommonRunPageRequest(request!.MaximumCount, null, request.Cursor), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Page(HumanReviewPageStatus.Unavailable);
        }

        if (page is null || page.Items is null || page.Items.Count > request.MaximumCount || page.ContinuationCursor is { Length: 0 or > CustomLoopLimits.MaxRunPageCursorCharacters })
        {
            return Page(HumanReviewPageStatus.Ambiguous);
        }

        var reviews = new List<HumanReviewSummary>(Math.Min(request.MaximumCount, page.Items.Count));
        foreach (var summary in page.Items)
        {
            if (summary is null || !CustomLoopArtifactIdentifier.IsValid(summary.Id))
            {
                return Page(HumanReviewPageStatus.Ambiguous);
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
            catch
            {
                return Page(HumanReviewPageStatus.Unavailable);
            }

            if (run is null || !string.Equals(run.Id, summary.Id, StringComparison.Ordinal))
            {
                return Page(HumanReviewPageStatus.Ambiguous);
            }

            if (run.HumanReview is null)
            {
                continue;
            }

            if (!TryProject(run, out var detail))
            {
                return Page(HumanReviewPageStatus.Corrupt);
            }

            reviews.Add(detail!.Summary);
        }

        return new HumanReviewPage(HumanReviewPageStatus.Ready, reviews, page.ContinuationCursor);
    }

    /// <summary>Reads one exact detached Human Review detail projection.</summary>
    /// <param name="runId">The exact durable run identity.</param>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>The detached detail, or a closed not-found, corrupt, invalid, or unavailable result.</returns>
    public async Task<HumanReviewReadResult> ReadAsync(string? runId, CancellationToken cancellationToken = default)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(runId))
        {
            return new HumanReviewReadResult(HumanReviewReadStatus.Invalid);
        }

        var read = await ReadRunAsync(runId!, cancellationToken).ConfigureAwait(false);
        if (read.Status != HumanReviewReadStatus.Ready)
        {
            return new HumanReviewReadResult(read.Status);
        }

        if (read.Run is null)
        {
            return new HumanReviewReadResult(HumanReviewReadStatus.NotFound);
        }

        if (!IsValidRun(read.Run))
        {
            return new HumanReviewReadResult(HumanReviewReadStatus.Corrupt);
        }

        if (read.Run.HumanReview is null)
        {
            return new HumanReviewReadResult(HumanReviewReadStatus.NotFound);
        }

        if (!TryProject(read.Run, out var detail))
        {
            return new HumanReviewReadResult(HumanReviewReadStatus.Corrupt);
        }

        detail = detail! with { EffectEvidence = await ReadEffectEvidenceAsync(read.Run, cancellationToken).ConfigureAwait(false) };
        return new HumanReviewReadResult(HumanReviewReadStatus.Ready, detail);
    }

    /// <summary>Reads the detached append-only evidence chain and current value-free effect posture.</summary>
    /// <param name="runId">The exact durable run identity.</param>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>The redacted evidence projection; it never claims release authority.</returns>
    public async Task<HumanReviewEvidenceReadResult> ReadEvidenceAsync(string? runId, CancellationToken cancellationToken = default)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(runId))
        {
            return EvidenceResult(HumanReviewEvidenceReadStatus.Invalid);
        }

        var read = await ReadRunAsync(runId!, cancellationToken).ConfigureAwait(false);
        if (read.Status != HumanReviewReadStatus.Ready)
        {
            return EvidenceResult(MapEvidenceReadStatus(read.Status));
        }

        if (read.Run is null)
        {
            return EvidenceResult(HumanReviewEvidenceReadStatus.NotFound);
        }

        if (!IsValidRun(read.Run))
        {
            return EvidenceResult(HumanReviewEvidenceReadStatus.Corrupt);
        }

        if (read.Run.HumanReview is null)
        {
            return EvidenceResult(HumanReviewEvidenceReadStatus.NotFound);
        }

        if (!TryProject(read.Run, out var detail))
        {
            return EvidenceResult(HumanReviewEvidenceReadStatus.Corrupt);
        }

        var effect = await ReadEffectEvidenceAsync(read.Run, cancellationToken).ConfigureAwait(false);
        return new HumanReviewEvidenceReadResult(HumanReviewEvidenceReadStatus.Ready, detail!.Evidence, effect);
    }

    /// <summary>Reads the current detached runtime posture for one exact Human Review run.</summary>
    /// <param name="runId">The exact durable run identity.</param>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>The current posture without binding or authority material.</returns>
    public async Task<HumanReviewRuntimePostureReadResult> ReadRuntimePostureAsync(string? runId, CancellationToken cancellationToken = default)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(runId))
        {
            return new HumanReviewRuntimePostureReadResult(HumanReviewReadStatus.Invalid, null);
        }

        var read = await ReadRunAsync(runId!, cancellationToken).ConfigureAwait(false);
        if (read.Status != HumanReviewReadStatus.Ready)
        {
            return new HumanReviewRuntimePostureReadResult(read.Status, null);
        }

        if (read.Run is null)
        {
            return new HumanReviewRuntimePostureReadResult(HumanReviewReadStatus.NotFound, null);
        }

        if (!IsValidRun(read.Run))
        {
            return new HumanReviewRuntimePostureReadResult(HumanReviewReadStatus.Corrupt, null);
        }

        if (read.Run.HumanReview is null)
        {
            return new HumanReviewRuntimePostureReadResult(HumanReviewReadStatus.NotFound, null);
        }

        if (!TryProject(read.Run, out var detail))
        {
            return new HumanReviewRuntimePostureReadResult(HumanReviewReadStatus.Corrupt, null);
        }

        return new HumanReviewRuntimePostureReadResult(HumanReviewReadStatus.Ready, detail!.Runtime);
    }

    /// <summary>Submits one authority-free Human Review decision operation through the canonical decision service.</summary>
    /// <param name="input">The run, version, operation, closed decision, and bounded detail supplied by the caller.</param>
    /// <param name="cancellationToken">Cancels the bounded operation.</param>
    /// <returns>The detached operation outcome and value-free receipt evidence.</returns>
    public async Task<HumanReviewDecisionResult> DecideAsync(HumanReviewDecisionOperationInput? input, CancellationToken cancellationToken = default)
    {
        if (input is null || !CustomLoopArtifactIdentifier.IsValid(input.RunId) || !HumanReviewIdentifier.IsValid(input.DecisionOperationId)
            || input.ExpectedLifecycleVersion < 1 || !Enum.IsDefined(input.Kind))
        {
            return new HumanReviewDecisionResult(HumanReviewDecisionStatus.Invalid, input?.DecisionOperationId ?? string.Empty, null);
        }

        HumanReviewDecisionServiceResult result;
        try
        {
            if (!TryMapDecisionKind(input.Kind, out var canonicalKind))
            {
                return new HumanReviewDecisionResult(HumanReviewDecisionStatus.Invalid, input.DecisionOperationId, null);
            }

            result = await _decisions.DecideAsync(new HumanReviewDecisionCommand(input.RunId, input.ExpectedLifecycleVersion, input.DecisionOperationId, canonicalKind, input.Detail), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new HumanReviewDecisionResult(HumanReviewDecisionStatus.Unavailable, input.DecisionOperationId, null);
        }

        if (result is null)
        {
            return new HumanReviewDecisionResult(HumanReviewDecisionStatus.Unavailable, input.DecisionOperationId, null);
        }

        try
        {
            return new HumanReviewDecisionResult(MapDecisionStatus(result.Status), input.DecisionOperationId, MapDecisionEvidence(result.Receipt));
        }
        catch
        {
            return new HumanReviewDecisionResult(HumanReviewDecisionStatus.Unavailable, input.DecisionOperationId, null);
        }
    }

    /// <summary>Submits one decision using only the authority-free public fields.</summary>
    /// <param name="runId">The target run identity.</param>
    /// <param name="expectedLifecycleVersion">The exact optimistic lifecycle version.</param>
    /// <param name="operationId">The client idempotency identity.</param>
    /// <param name="kind">The closed decision kind.</param>
    /// <param name="detail">Optional bounded redacted detail.</param>
    /// <param name="cancellationToken">Cancels the bounded operation.</param>
    /// <returns>The detached operation outcome.</returns>
    public Task<HumanReviewDecisionResult> DecideAsync(string? runId, int expectedLifecycleVersion, string? operationId, HumanReviewDecisionKind kind, string? detail = null, CancellationToken cancellationToken = default)
        => DecideAsync(new HumanReviewDecisionOperationInput(runId ?? string.Empty, expectedLifecycleVersion, operationId ?? string.Empty, kind, detail), cancellationToken);

    private async Task<(HumanReviewReadStatus Status, CustomLoopRunRecord? Run)> ReadRunAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                return (HumanReviewReadStatus.NotFound, null);
            }

            return string.Equals(run.Id, runId, StringComparison.Ordinal)
                ? (HumanReviewReadStatus.Ready, run)
                : (HumanReviewReadStatus.Corrupt, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (HumanReviewReadStatus.Unavailable, null);
        }
    }

    private async Task<HumanReviewEffectEvidence> ReadEffectEvidenceAsync(CustomLoopRunRecord run, CancellationToken cancellationToken)
    {
        try
        {
            var binding = run.HumanReview!.Request.Binding;
            if (binding.EffectAttempt is null)
            {
                return new HumanReviewEffectEvidence(HumanReviewEffectEvidenceStatus.Missing, null, null, null, null);
            }

            if (_effectEvidence is null || _effectCertainty is null)
            {
                return new HumanReviewEffectEvidence(HumanReviewEffectEvidenceStatus.Unavailable, null, null, null, binding.EffectAttempt.EffectAttemptId);
            }

            HumanReviewCurrentEffectAttemptEvidenceReadResult current;
            try
            {
                current = await _effectEvidence.ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(binding, binding.EffectAttempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new HumanReviewEffectEvidence(HumanReviewEffectEvidenceStatus.Unavailable, null, null, null, binding.EffectAttempt.EffectAttemptId);
            }

            if (current.Status != HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current || current.Evidence is null)
            {
                return new HumanReviewEffectEvidence(MapEffectEvidenceStatus(current.Status), null, null, null, binding.EffectAttempt.EffectAttemptId);
            }

            GovernedLoopEffectCertaintySnapshotResult certainty;
            var query = new GovernedLoopEffectCertaintySnapshotQuery(current.Evidence.Identity, current.Evidence.Preparation);
            try
            {
                certainty = await _effectCertainty.ReadAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new HumanReviewEffectEvidence(HumanReviewEffectEvidenceStatus.Unavailable, null, current.Evidence.Identity.IdentityHash, current.Evidence.Preparation.PreparationHash, binding.EffectAttempt.EffectAttemptId);
            }

            var status = HumanReviewEffectReleaseReadStatusProjection.Project(query, certainty);
            var snapshot = certainty.Snapshot;
            return new HumanReviewEffectEvidence(MapEffectReleaseStatus(status), MapEffectCertainty(snapshot?.Certainty), current.Evidence.Identity.IdentityHash, current.Evidence.Preparation.PreparationHash, binding.EffectAttempt.EffectAttemptId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var effectAttemptId = run.HumanReview?.Request.Binding.EffectAttempt?.EffectAttemptId;
            return new HumanReviewEffectEvidence(HumanReviewEffectEvidenceStatus.Unavailable, null, null, null, effectAttemptId);
        }
    }

    private static bool TryProject(CustomLoopRunRecord run, out HumanReviewDetail? detail)
    {
        detail = null;
        try
        {
            var review = run.HumanReview;
            if (review is null || !IsValidRun(run))
            {
                return false;
            }

            var summary = new HumanReviewSummary(
                run.Id,
                review.Request.RequestId,
                review.Request.RequestHash,
                MapPurpose(review.Request.Purpose),
                review.Request.RequestedDecisions.Select(MapDecisionKind).ToImmutableArray(),
                MapLifecycleStatus(review.Lifecycle.Status),
                MapRunStatus(run.Status),
                MapFrontierStatus(run.Frontier?.Payload.Status),
                run.LifecycleVersion,
                run.UpdatedAtUtc,
                review.Request.Timing.ExpiresAtUtc);
            var previews = review.Request.Previews.Select(MapPreview).ToArray();
            var decisions = review.AcceptedDecisions.Select(MapDecision).ToArray();
            var evidence = review.Evidence.Select(MapEvidence).ToArray();
            var runtime = new HumanReviewRuntimePosture(
                MapRunStatus(run.Status),
                MapFrontierStatus(run.Frontier?.Payload.Status),
                MapLifecycleStatus(review.Lifecycle.Status),
                MapContinuationStatus(review),
                run.LifecycleVersion,
                evidence.Length,
                decisions.Length,
                review.DecisionActions.Length,
                run.UpdatedAtUtc);
            detail = new HumanReviewDetail(summary, previews, decisions, evidence, runtime, null);
            return true;
        }
        catch
        {
            detail = null;
            return false;
        }
    }

    private static HumanReviewPreview MapPreview(CommonRedactedPreview preview)
        => new(MapPreviewKind(preview.Kind), preview.Label, preview.Detail, preview.DetailHash);

    private static bool IsValidRun(CustomLoopRunRecord run)
    {
        try
        {
            return CustomLoopRunValidator.Validate(run).IsValid;
        }
        catch
        {
            return false;
        }
    }

    private static HumanReviewDecisionProjection MapDecision(CommonDecision decision)
        => new(decision.DecisionId, decision.DecisionOperationId, MapDecisionKind(decision.Kind), decision.DecidedAtUtc, decision.Detail, decision.DecisionHash);

    private static HumanReviewEvidenceProjection MapEvidence(CommonEvidence evidence)
        => new(evidence.EvidenceId, MapEvidenceKind(evidence.Kind), evidence.RecordedAtUtc, evidence.DecisionOperation?.DecisionOperationId, evidence.Decision is null ? null : MapDecisionKind(evidence.Decision.Kind), evidence.Previews.Select(MapPreview).ToArray(), evidence.EvidenceHash);

    private static HumanReviewDecisionEvidence? MapDecisionEvidence(CommonDecisionOperationReceipt? receipt)
        => receipt is null
            ? null
            : new HumanReviewDecisionEvidence(receipt.DecisionOperationId, receipt.Request.RequestId, MapDecisionOperationDisposition(receipt.Disposition), receipt.Decision is null ? null : MapDecisionKind(receipt.Decision.Kind), receipt.RecordedAtUtc, receipt.ProposalHash, receipt.ReceiptHash);

    private static HumanReviewContinuationStatus MapContinuationStatus(CommonRunState review)
    {
        if (review.ContinuationReservation is null)
        {
            return HumanReviewContinuationStatus.NotReserved;
        }
        if (review.Continuation is null)
        {
            return HumanReviewContinuationStatus.Reserved;
        }
        if (!HumanReviewContinuationContractValidator.ValidateState(review.Request, review.ContinuationReservation, review.Continuation).IsValid)
        {
            return HumanReviewContinuationStatus.Corrupt;
        }
        if (review.Continuation.Completion is not null)
        {
            return HumanReviewContinuationStatus.Completed;
        }
        if (review.Continuation.Retirement is not null)
        {
            return HumanReviewContinuationStatus.Retired;
        }
        return review.Continuation.Claims.Length > 0 ? HumanReviewContinuationStatus.Claimed : HumanReviewContinuationStatus.Published;
    }

    private static bool TryMapDecisionKind(HumanReviewDecisionKind source, out CommonDecisionKind mapped)
    {
        mapped = source switch
        {
            HumanReviewDecisionKind.Approve => CommonDecisionKind.Approve,
            HumanReviewDecisionKind.Reject => CommonDecisionKind.Reject,
            HumanReviewDecisionKind.Cancel => CommonDecisionKind.Cancel,
            HumanReviewDecisionKind.RequestInformation => CommonDecisionKind.RequestInformation,
            _ => CommonDecisionKind.Unknown
        };
        return mapped != CommonDecisionKind.Unknown;
    }

    private static HumanReviewDecisionKind MapDecisionKind(CommonDecisionKind source)
        => source switch
        {
            CommonDecisionKind.Approve => HumanReviewDecisionKind.Approve,
            CommonDecisionKind.Reject => HumanReviewDecisionKind.Reject,
            CommonDecisionKind.Cancel => HumanReviewDecisionKind.Cancel,
            CommonDecisionKind.RequestInformation => HumanReviewDecisionKind.RequestInformation,
            _ => HumanReviewDecisionKind.Unknown
        };

    private static HumanReviewPreviewKind MapPreviewKind(CommonPreviewKind source)
        => source switch
        {
            CommonPreviewKind.Action => HumanReviewPreviewKind.Action,
            CommonPreviewKind.Result => HumanReviewPreviewKind.Result,
            CommonPreviewKind.Evidence => HumanReviewPreviewKind.Evidence,
            _ => HumanReviewPreviewKind.Unknown
        };

    private static HumanReviewEvidenceKind MapEvidenceKind(CommonEvidenceKind source)
        => source switch
        {
            CommonEvidenceKind.RequestAdmitted => HumanReviewEvidenceKind.RequestAdmitted,
            CommonEvidenceKind.RequestPublished => HumanReviewEvidenceKind.RequestPublished,
            CommonEvidenceKind.ReminderRecorded => HumanReviewEvidenceKind.ReminderRecorded,
            CommonEvidenceKind.EscalationRecorded => HumanReviewEvidenceKind.EscalationRecorded,
            CommonEvidenceKind.DecisionAttempted => HumanReviewEvidenceKind.DecisionAttempted,
            CommonEvidenceKind.DecisionAccepted => HumanReviewEvidenceKind.DecisionAccepted,
            CommonEvidenceKind.InformationRequested => HumanReviewEvidenceKind.InformationRequested,
            CommonEvidenceKind.DecisionConflict => HumanReviewEvidenceKind.DecisionConflict,
            CommonEvidenceKind.RequestConflict => HumanReviewEvidenceKind.RequestConflict,
            CommonEvidenceKind.RequestExpired => HumanReviewEvidenceKind.RequestExpired,
            CommonEvidenceKind.RequestSuperseded => HumanReviewEvidenceKind.RequestSuperseded,
            CommonEvidenceKind.ContinuationReserved => HumanReviewEvidenceKind.ContinuationReserved,
            CommonEvidenceKind.ContinuationCompleted => HumanReviewEvidenceKind.ContinuationCompleted,
            CommonEvidenceKind.PreDispatchBlocked => HumanReviewEvidenceKind.PreDispatchBlocked,
            CommonEvidenceKind.DecisionDenied => HumanReviewEvidenceKind.DecisionDenied,
            CommonEvidenceKind.DecisionExpired => HumanReviewEvidenceKind.DecisionExpired,
            _ => HumanReviewEvidenceKind.Unknown
        };

    private static HumanReviewPurpose MapPurpose(CommonPurpose source)
        => source switch
        {
            CommonPurpose.Continuation => HumanReviewPurpose.Continuation,
            CommonPurpose.PreDispatchEffect => HumanReviewPurpose.PreDispatchEffect,
            _ => HumanReviewPurpose.Unknown
        };

    private static HumanReviewLifecycleStatus MapLifecycleStatus(CommonLifecycleStatus source)
        => source switch
        {
            CommonLifecycleStatus.Pending => HumanReviewLifecycleStatus.Pending,
            CommonLifecycleStatus.AwaitingInformation => HumanReviewLifecycleStatus.AwaitingInformation,
            CommonLifecycleStatus.Approved => HumanReviewLifecycleStatus.Approved,
            CommonLifecycleStatus.Rejected => HumanReviewLifecycleStatus.Rejected,
            CommonLifecycleStatus.Cancelled => HumanReviewLifecycleStatus.Cancelled,
            CommonLifecycleStatus.Expired => HumanReviewLifecycleStatus.Expired,
            CommonLifecycleStatus.Superseded => HumanReviewLifecycleStatus.Superseded,
            CommonLifecycleStatus.Conflicted => HumanReviewLifecycleStatus.Conflicted,
            _ => HumanReviewLifecycleStatus.Unknown
        };

    private static CustomLoopRunStatus MapRunStatus(CommonRunStatus source)
        => source switch
        {
            CommonRunStatus.Admitted => CustomLoopRunStatus.Admitted,
            CommonRunStatus.Running => CustomLoopRunStatus.Running,
            CommonRunStatus.PauseRequested => CustomLoopRunStatus.PauseRequested,
            CommonRunStatus.Paused => CustomLoopRunStatus.Paused,
            CommonRunStatus.CancelRequested => CustomLoopRunStatus.CancelRequested,
            CommonRunStatus.Completed => CustomLoopRunStatus.Completed,
            CommonRunStatus.Failed => CustomLoopRunStatus.Failed,
            CommonRunStatus.Cancelled => CustomLoopRunStatus.Cancelled,
            CommonRunStatus.NeedsReview => CustomLoopRunStatus.NeedsReview,
            CommonRunStatus.Waiting => CustomLoopRunStatus.Waiting,
            _ => CustomLoopRunStatus.Unknown
        };

    private static GovernedLoopFrontierStatus MapFrontierStatus(CommonFrontierStatus? source)
        => source switch
        {
            CommonFrontierStatus.Active => GovernedLoopFrontierStatus.Active,
            CommonFrontierStatus.Waiting => GovernedLoopFrontierStatus.Waiting,
            CommonFrontierStatus.ReviewBlocked => GovernedLoopFrontierStatus.ReviewBlocked,
            CommonFrontierStatus.Completed => GovernedLoopFrontierStatus.Completed,
            CommonFrontierStatus.Failed => GovernedLoopFrontierStatus.Failed,
            CommonFrontierStatus.Cancelled => GovernedLoopFrontierStatus.Cancelled,
            _ => GovernedLoopFrontierStatus.Unknown
        };

    private static HumanReviewDecisionOperationDisposition MapDecisionOperationDisposition(CommonDecisionOperationDisposition source)
        => source switch
        {
            CommonDecisionOperationDisposition.Accepted => HumanReviewDecisionOperationDisposition.Accepted,
            CommonDecisionOperationDisposition.InformationRequested => HumanReviewDecisionOperationDisposition.InformationRequested,
            CommonDecisionOperationDisposition.Denied => HumanReviewDecisionOperationDisposition.Denied,
            CommonDecisionOperationDisposition.Conflict => HumanReviewDecisionOperationDisposition.Conflict,
            CommonDecisionOperationDisposition.Expired => HumanReviewDecisionOperationDisposition.Expired,
            _ => HumanReviewDecisionOperationDisposition.Unknown
        };

    private static HumanReviewEffectCertainty? MapEffectCertainty(CommonEffectCertainty? source)
        => source switch
        {
            CommonEffectCertainty.NotStarted => HumanReviewEffectCertainty.NotStarted,
            CommonEffectCertainty.Dispatched => HumanReviewEffectCertainty.Dispatched,
            CommonEffectCertainty.Conclusive => HumanReviewEffectCertainty.Conclusive,
            CommonEffectCertainty.Ambiguous => HumanReviewEffectCertainty.Ambiguous,
            CommonEffectCertainty.Terminal => HumanReviewEffectCertainty.Terminal,
            CommonEffectCertainty.Unknown => HumanReviewEffectCertainty.Unknown,
            null => null,
            _ => HumanReviewEffectCertainty.Unknown
        };

    private static bool IsValidPageRequest(HumanReviewPageRequest? request)
        => request is not null
            && request.MaximumCount is > 0 and <= MaxPageSize
            && (request.Cursor is null || request.Cursor.Length is > 0 and <= CustomLoopLimits.MaxRunPageCursorCharacters && !string.IsNullOrWhiteSpace(request.Cursor));

    private static HumanReviewPage Page(HumanReviewPageStatus status) => new(status, [], null);

    private static HumanReviewEvidenceReadResult EvidenceResult(HumanReviewEvidenceReadStatus status) => new(status, [], null);

    private static HumanReviewDecisionStatus MapDecisionStatus(HumanReviewDecisionServiceStatus status)
        => status switch
        {
            HumanReviewDecisionServiceStatus.Accepted => HumanReviewDecisionStatus.Accepted,
            HumanReviewDecisionServiceStatus.InformationRequested => HumanReviewDecisionStatus.InformationRequested,
            HumanReviewDecisionServiceStatus.Denied => HumanReviewDecisionStatus.Denied,
            HumanReviewDecisionServiceStatus.Conflict => HumanReviewDecisionStatus.Conflict,
            HumanReviewDecisionServiceStatus.Expired => HumanReviewDecisionStatus.Expired,
            HumanReviewDecisionServiceStatus.Replayed => HumanReviewDecisionStatus.Replayed,
            HumanReviewDecisionServiceStatus.NotFound => HumanReviewDecisionStatus.NotFound,
            HumanReviewDecisionServiceStatus.Invalid => HumanReviewDecisionStatus.Invalid,
            HumanReviewDecisionServiceStatus.LimitExceeded => HumanReviewDecisionStatus.LimitExceeded,
            _ => HumanReviewDecisionStatus.Unavailable,
        };

    private static HumanReviewEvidenceReadStatus MapEvidenceReadStatus(HumanReviewReadStatus status)
        => status switch
        {
            HumanReviewReadStatus.NotFound => HumanReviewEvidenceReadStatus.NotFound,
            HumanReviewReadStatus.Corrupt => HumanReviewEvidenceReadStatus.Corrupt,
            HumanReviewReadStatus.Invalid => HumanReviewEvidenceReadStatus.Invalid,
            _ => HumanReviewEvidenceReadStatus.Unavailable,
        };

    private static HumanReviewEffectEvidenceStatus MapEffectEvidenceStatus(HumanReviewCurrentEffectAttemptEvidenceReadStatus status)
        => status switch
        {
            HumanReviewCurrentEffectAttemptEvidenceReadStatus.Missing => HumanReviewEffectEvidenceStatus.Missing,
            HumanReviewCurrentEffectAttemptEvidenceReadStatus.Corrupt => HumanReviewEffectEvidenceStatus.Corrupt,
            HumanReviewCurrentEffectAttemptEvidenceReadStatus.Stale => HumanReviewEffectEvidenceStatus.Stale,
            HumanReviewCurrentEffectAttemptEvidenceReadStatus.Unavailable => HumanReviewEffectEvidenceStatus.Unavailable,
            _ => HumanReviewEffectEvidenceStatus.Invalid,
        };

    private static HumanReviewEffectEvidenceStatus MapEffectReleaseStatus(HumanReviewEffectReleaseReadStatus status)
        => status switch
        {
            HumanReviewEffectReleaseReadStatus.ExactNotStarted => HumanReviewEffectEvidenceStatus.ExactNotStarted,
            HumanReviewEffectReleaseReadStatus.Dispatched => HumanReviewEffectEvidenceStatus.Dispatched,
            HumanReviewEffectReleaseReadStatus.Conclusive => HumanReviewEffectEvidenceStatus.Conclusive,
            HumanReviewEffectReleaseReadStatus.Ambiguous => HumanReviewEffectEvidenceStatus.Ambiguous,
            HumanReviewEffectReleaseReadStatus.Terminal => HumanReviewEffectEvidenceStatus.Terminal,
            HumanReviewEffectReleaseReadStatus.Missing => HumanReviewEffectEvidenceStatus.Missing,
            HumanReviewEffectReleaseReadStatus.Corrupt => HumanReviewEffectEvidenceStatus.Corrupt,
            HumanReviewEffectReleaseReadStatus.Unavailable => HumanReviewEffectEvidenceStatus.Unavailable,
            HumanReviewEffectReleaseReadStatus.Stale => HumanReviewEffectEvidenceStatus.Stale,
            _ => HumanReviewEffectEvidenceStatus.Invalid,
        };
}
