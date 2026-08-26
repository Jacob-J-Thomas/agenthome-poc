using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Atomically records authenticated Human Review decisions while preserving the exact paused ReviewBlocked frontier.</summary>
public sealed class HumanReviewDecisionService : IHumanReviewDecisionService
{
    private const int MaximumAttempts = 3;
    private readonly ICustomLoopRunStore _runs;
    private readonly IHumanReviewDecisionAuthorizer _authorizer;
    private readonly IHumanReviewTrustedClock _clock;

    /// <summary>Initializes the decision service.</summary>
    /// <param name="runs">The sole durable run transaction boundary.</param>
    /// <param name="authorizer">The server-owned reviewer authorization boundary.</param>
    /// <param name="clock">The trusted UTC clock.</param>
    /// <exception cref="ArgumentNullException">Thrown when any dependency is <see langword="null"/>.</exception>
    public HumanReviewDecisionService(ICustomLoopRunStore runs, IHumanReviewDecisionAuthorizer authorizer, IHumanReviewTrustedClock clock)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<HumanReviewDecisionServiceResult> DecideAsync(HumanReviewDecisionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CustomLoopArtifactIdentifier.IsValid(command.RunId) || command.ExpectedLifecycleVersion < 1 || command.ExpectedLifecycleVersion > HumanReviewContractLimits.MaxVersion)
        {
            return Result(HumanReviewDecisionServiceStatus.Invalid);
        }

        var proposal = CreateProposal(command);
        if (proposal is null)
        {
            return Result(HumanReviewDecisionServiceStatus.Invalid);
        }

        var predecessorWasStale = false;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            CustomLoopRunRecord? current;
            try
            {
                current = await _runs.GetAsync(command.RunId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Result(HumanReviewDecisionServiceStatus.Unavailable);
            }

            if (current is null)
            {
                return Result(HumanReviewDecisionServiceStatus.NotFound);
            }

            if (!string.Equals(current.Id, command.RunId, StringComparison.Ordinal) || !IsCanonicalReviewBlockedRun(current, out var state))
            {
                return Result(HumanReviewDecisionServiceStatus.Invalid);
            }

            if (!TryGetTrustedNow(current, out var atUtc))
            {
                return Result(HumanReviewDecisionServiceStatus.Unavailable);
            }

            if (!HumanReviewContractSnapshot.TryCaptureRequest(state.Request, out var requestSnapshot, out _) || requestSnapshot is null)
            {
                return Result(HumanReviewDecisionServiceStatus.Invalid);
            }

            var authorizationRequest = new HumanReviewDecisionAuthorizationRequest(requestSnapshot, proposal, requestSnapshot.RequestHash, proposal.DecisionOperationId, proposal.ProposalHash, atUtc);
            var authorization = await AuthorizeAsync(authorizationRequest, cancellationToken);
            if (!IsBoundAuthorization(authorizationRequest, authorization))
            {
                return Result(HumanReviewDecisionServiceStatus.Unavailable);
            }

            if (!authorization!.IsAuthorized)
            {
                return Result(HumanReviewDecisionServiceStatus.Denied);
            }

            var permitted = IsEligible(state.Request, proposal, authorization);
            // TODO(#553): Enforce or explicitly narrow cross-run operation uniqueness once Phase 4 owns a canonical global index: https://github.com/Jacob-J-Thomas/agenthome-poc/issues/553
            var existing = state.OperationReceipts.SingleOrDefault(item => string.Equals(item.DecisionOperationId, proposal.DecisionOperationId, StringComparison.Ordinal));
            if (existing is not null)
            {
                // Authorization is intentionally re-evaluated before a durable replay can be disclosed.
                return !permitted
                        ? Result(HumanReviewDecisionServiceStatus.Denied)
                        : string.Equals(existing.ProposalHash, proposal.ProposalHash, StringComparison.Ordinal)
                        ? Result(HumanReviewDecisionServiceStatus.Replayed, CopyReceipt(existing))
                        : Result(HumanReviewDecisionServiceStatus.Conflict);
            }

            if (current.IsTerminal)
            {
                return Result(HumanReviewDecisionServiceStatus.Conflict);
            }

            predecessorWasStale |= current.LifecycleVersion != command.ExpectedLifecycleVersion;

            var disposition = SelectDisposition(state, proposal, permitted, predecessorWasStale, atUtc);
            if (!CanAppend(current, state, proposal, disposition))
            {
                return Result(HumanReviewDecisionServiceStatus.LimitExceeded);
            }

            if (!TryCreateCandidate(current, state, proposal, authorization, disposition, atUtc, out var next, out var receipt))
            {
                return Result(HumanReviewDecisionServiceStatus.Invalid);
            }

            if (!IsCanonicalSuccessor(current, next))
            {
                return Result(HumanReviewDecisionServiceStatus.Invalid);
            }

            CustomLoopRunStoreResult committed;
            try
            {
                committed = await _runs.UpdateAsync(next, current.LifecycleVersion, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var recovered = await TryRecoverExactOperationAsync(command.RunId, state.Request.RequestHash, proposal, permitted);
                if (recovered is not null)
                {
                    return recovered;
                }

                throw;
            }
            catch
            {
                if (attempt + 1 == MaximumAttempts)
                {
                    var recovered = await TryRecoverExactOperationAsync(command.RunId, state.Request.RequestHash, proposal, permitted);
                    if (recovered is not null)
                    {
                        return recovered;
                    }

                    return Result(HumanReviewDecisionServiceStatus.Unavailable);
                }

                continue;
            }

            if (committed is null)
            {
                return Result(HumanReviewDecisionServiceStatus.Unavailable);
            }

            if (committed.Status == CustomLoopRunStoreStatus.Updated)
            {
                if (TryGetExactCommittedReceipt(next, committed.Run, receipt, out var committedReceipt))
                {
                    return Result(StatusFor(disposition), committedReceipt);
                }

                if (attempt + 1 == MaximumAttempts)
                {
                    var recovered = await TryRecoverExactOperationAsync(command.RunId, state.Request.RequestHash, proposal, permitted);
                    return recovered ?? Result(HumanReviewDecisionServiceStatus.Unavailable);
                }

                continue;
            }

            if (committed.Status == CustomLoopRunStoreStatus.LimitExceeded)
            {
                return Result(HumanReviewDecisionServiceStatus.LimitExceeded);
            }

            if (committed.Status == CustomLoopRunStoreStatus.NotFound)
            {
                return Result(HumanReviewDecisionServiceStatus.NotFound);
            }

            if (committed.Status is not (CustomLoopRunStoreStatus.Conflict or CustomLoopRunStoreStatus.OperationConflict or CustomLoopRunStoreStatus.TerminalImmutable))
            {
                return Result(HumanReviewDecisionServiceStatus.Unavailable);
            }

            if (attempt + 1 == MaximumAttempts)
            {
                return Result(HumanReviewDecisionServiceStatus.Conflict);
            }
        }

        return Result(HumanReviewDecisionServiceStatus.Unavailable);
    }

    private static HumanReviewDecisionProposal? CreateProposal(HumanReviewDecisionCommand command)
    {
        try
        {
            var proposal = HumanReviewContractHash.ApplyDecisionProposal(new HumanReviewDecisionProposal(1, command.DecisionOperationId, command.Kind, command.Detail, string.Empty));
            return HumanReviewContractValidator.ValidateDecisionProposal(proposal).IsValid ? proposal : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<HumanReviewDecisionAuthorization?> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _authorizer.AuthorizeAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetTrustedNow(CustomLoopRunRecord current, out DateTimeOffset atUtc)
    {
        try
        {
            atUtc = _clock.UtcNow;
            return atUtc != default && atUtc.Offset == TimeSpan.Zero && atUtc >= current.UpdatedAtUtc;
        }
        catch
        {
            atUtc = default;
            return false;
        }
    }

    private static bool IsCanonicalReviewBlockedRun(CustomLoopRunRecord current, out HumanReviewRunState state)
    {
        state = null!;
        try
        {
            if (current.HumanReview is not { } review || current.Status != CustomLoopRunStatus.Paused || current.Frontier?.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked || !CustomLoopRunValidator.Validate(current).IsValid)
            {
                return false;
            }

            state = review;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBoundAuthorization(HumanReviewDecisionAuthorizationRequest request, HumanReviewDecisionAuthorization? authorization)
        => authorization is not null
            && string.Equals(authorization.RequestHash, request.RequestHash, StringComparison.Ordinal)
            && string.Equals(authorization.DecisionOperationId, request.DecisionOperationId, StringComparison.Ordinal)
            && string.Equals(authorization.ProposalHash, request.ProposalHash, StringComparison.Ordinal)
            && authorization.EvaluatedAtUtc == request.EvaluatedAtUtc
            && authorization.EvaluatedAtUtc != default
            && authorization.EvaluatedAtUtc.Offset == TimeSpan.Zero
            && (!authorization.IsAuthorized || HasValidAuthorizedIdentity(authorization));

    private static bool HasValidAuthorizedIdentity(HumanReviewDecisionAuthorization authorization)
        => HumanReviewIdentifier.IsValid(authorization.ActorId)
            && HumanReviewIdentifier.IsValid(authorization.ReviewerRoleId)
            && HumanReviewIdentifier.IsValid(authorization.CorrelationId)
            && HasCanonicalScopes(authorization.ScopeIds);

    private static bool HasCanonicalScopes(ImmutableArray<string> scopeIds)
    {
        if (scopeIds.IsDefault || scopeIds.Length is < 1 or > HumanReviewContractLimits.MaxScopesPerReviewer)
        {
            return false;
        }

        for (var index = 0; index < scopeIds.Length; index++)
        {
            if (!HumanReviewIdentifier.IsValid(scopeIds[index]) || index > 0 && string.CompareOrdinal(scopeIds[index - 1], scopeIds[index]) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEligible(HumanReviewRequest request, HumanReviewDecisionProposal proposal, HumanReviewDecisionAuthorization authorization)
        => HasValidAuthorizedIdentity(authorization)
            && request.RequestedDecisions.Contains(proposal.Kind)
            && request.EligibleReviewers.Any(item => string.Equals(item.ReviewerRoleId, authorization.ReviewerRoleId, StringComparison.Ordinal) && item.ScopeIds.SequenceEqual(authorization.ScopeIds, StringComparer.Ordinal));

    private static HumanReviewDecisionOperationDisposition SelectDisposition(HumanReviewRunState state, HumanReviewDecisionProposal proposal, bool permitted, bool stalePredecessor, DateTimeOffset atUtc)
    {
        if (!permitted)
        {
            return HumanReviewDecisionOperationDisposition.Denied;
        }

        if (state.AcceptedTerminalDecision is not null)
        {
            return HumanReviewDecisionOperationDisposition.Conflict;
        }

        if (atUtc >= state.Request.Timing.ExpiresAtUtc)
        {
            return HumanReviewDecisionOperationDisposition.Expired;
        }

        if (stalePredecessor)
        {
            return HumanReviewDecisionOperationDisposition.Conflict;
        }

        return proposal.Kind == HumanReviewDecisionKind.RequestInformation
            ? HumanReviewDecisionOperationDisposition.InformationRequested
            : HumanReviewDecisionOperationDisposition.Accepted;
    }

    private static bool CanAppend(CustomLoopRunRecord current, HumanReviewRunState state, HumanReviewDecisionProposal proposal, HumanReviewDecisionOperationDisposition disposition)
    {
        var eventCount = disposition == HumanReviewDecisionOperationDisposition.Accepted && proposal.Kind == HumanReviewDecisionKind.Approve ? 2 : 1;
        if (current.LifecycleVersion == int.MaxValue || current.Events.Length > CustomLoopLimits.MaxTraceEventsPerRun - eventCount || state.OperationReceipts.Length >= HumanReviewContractLimits.MaxDecisionOperationReceipts)
        {
            return false;
        }

        var acceptsDecision = disposition is HumanReviewDecisionOperationDisposition.Accepted or HumanReviewDecisionOperationDisposition.InformationRequested;
        if (acceptsDecision && state.AcceptedDecisions.Length >= HumanReviewContractLimits.MaxAcceptedDecisions)
        {
            return false;
        }

        var appendsLifecycle = acceptsDecision || (disposition == HumanReviewDecisionOperationDisposition.Expired && state.Lifecycle.Status != HumanReviewLifecycleStatus.Expired);
        return !appendsLifecycle || state.LifecycleHistory.Length < HumanReviewContractLimits.MaxLifecycleHistory;
    }

    private static bool TryCreateCandidate(
        CustomLoopRunRecord current,
        HumanReviewRunState state,
        HumanReviewDecisionProposal proposal,
        HumanReviewDecisionAuthorization authorization,
        HumanReviewDecisionOperationDisposition disposition,
        DateTimeOffset atUtc,
        out CustomLoopRunRecord next,
        out HumanReviewDecisionOperationReceipt receipt)
    {
        next = null!;
        receipt = null!;
        try
        {
            var requestReference = new HumanReviewRequestReference(state.Request.RequestId, state.Request.RequestHash);
            var acceptsDecision = disposition is HumanReviewDecisionOperationDisposition.Accepted or HumanReviewDecisionOperationDisposition.InformationRequested;
            var decision = acceptsDecision ? CreateDecision(state.Request, proposal, authorization, atUtc) : null;
            var decisionReference = decision is null ? null : Reference(decision);
            var correlationId = authorization.CorrelationId!;
            receipt = HumanReviewContractHash.ApplyDecisionOperationReceipt(new HumanReviewDecisionOperationReceipt(1, proposal.DecisionOperationId, proposal.ProposalHash, requestReference, disposition, decisionReference, atUtc, ServerProvenance(correlationId, atUtc), string.Empty));
            var evidence = CreateOperationEvidence(state, requestReference, receipt, decisionReference, atUtc);
            var history = state.LifecycleHistory;
            if (decision is not null || (disposition == HumanReviewDecisionOperationDisposition.Expired && state.Lifecycle.Status != HumanReviewLifecycleStatus.Expired))
            {
                history = [.. history, CreateLifecycle(state, requestReference, decisionReference, decision?.Kind, disposition, correlationId, atUtc)];
            }

            var reservation = state.ContinuationReservation;
            HumanReviewEvidence? reservationEvidence = null;
            if (decision?.Kind == HumanReviewDecisionKind.Approve)
            {
                reservation = HumanReviewContractHash.ApplyContinuationReservation(new HumanReviewContinuationReservation(1, Id("reservation", decision.DecisionHash), requestReference, decisionReference!, atUtc, ServerProvenance(correlationId, atUtc), string.Empty));
                reservationEvidence = HumanReviewContractHash.ApplyEvidence(new HumanReviewEvidence(1, Id("evidence", reservation.ReservationHash), requestReference, HumanReviewEvidenceKind.ContinuationReserved, decisionReference, atUtc, ServerProvenance(correlationId, atUtc), ImmutableArray<HumanReviewRedactedPreview>.Empty, evidence.EvidenceHash, string.Empty)
                {
                    ContinuationReservation = new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash)
                });
            }

            var terminal = decision is { Kind: not HumanReviewDecisionKind.RequestInformation } ? decision : state.AcceptedTerminalDecision;
            var nextState = state with
            {
                Lifecycle = history[^1],
                LifecycleHistory = history,
                OperationReceipts = [.. state.OperationReceipts, receipt],
                AcceptedDecisions = decision is null ? state.AcceptedDecisions : [.. state.AcceptedDecisions, decision],
                AcceptedTerminalDecision = terminal,
                ContinuationReservation = reservation,
                Evidence = reservationEvidence is null ? [.. state.Evidence, evidence] : [.. state.Evidence, evidence, reservationEvidence]
            };
            var operationEvent = CreateOperationEvent(current, evidence, receipt);
            var reservationEvent = reservationEvidence is null ? null : CreateReservationEvent(current, reservationEvidence);
            next = current with
            {
                LifecycleVersion = checked(current.LifecycleVersion + 1),
                UpdatedAtUtc = atUtc,
                HumanReview = nextState,
                Events = reservationEvent is null ? [.. current.Events, operationEvent] : [.. current.Events, operationEvent, reservationEvent]
            };
            return true;
        }
        catch
        {
            next = null!;
            receipt = null!;
            return false;
        }
    }

    private static HumanReviewDecision CreateDecision(HumanReviewRequest request, HumanReviewDecisionProposal proposal, HumanReviewDecisionAuthorization authorization, DateTimeOffset atUtc)
        => HumanReviewContractHash.ApplyDecision(new HumanReviewDecision(1, Id("decision", proposal.ProposalHash), proposal.DecisionOperationId, new HumanReviewRequestReference(request.RequestId, request.RequestHash), proposal.Kind, authorization.ActorId!, authorization.ReviewerRoleId!, ImmutableCollectionsMarshal.AsImmutableArray(authorization.ScopeIds.AsSpan().ToArray()), atUtc, proposal.Detail, HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.AuthenticatedReviewer, authorization.ActorId!, authorization.CorrelationId!, atUtc, string.Empty)), string.Empty));

    private static HumanReviewEvidence CreateOperationEvidence(HumanReviewRunState state, HumanReviewRequestReference request, HumanReviewDecisionOperationReceipt receipt, HumanReviewDecisionReference? decision, DateTimeOffset atUtc)
    {
        var kind = receipt.Disposition switch
        {
            HumanReviewDecisionOperationDisposition.Accepted => HumanReviewEvidenceKind.DecisionAccepted,
            HumanReviewDecisionOperationDisposition.InformationRequested => HumanReviewEvidenceKind.InformationRequested,
            HumanReviewDecisionOperationDisposition.Conflict => HumanReviewEvidenceKind.DecisionConflict,
            HumanReviewDecisionOperationDisposition.Expired => HumanReviewEvidenceKind.DecisionExpired,
            _ => HumanReviewEvidenceKind.DecisionDenied
        };
        return HumanReviewContractHash.ApplyEvidence(new HumanReviewEvidence(1, Id("evidence", receipt.ReceiptHash), request, kind, decision, atUtc, ServerProvenance(receipt.Provenance.CorrelationId, atUtc), ImmutableArray<HumanReviewRedactedPreview>.Empty, state.Evidence[^1].EvidenceHash, string.Empty)
        {
            DecisionOperation = new HumanReviewDecisionOperationReference(receipt.DecisionOperationId, receipt.ProposalHash, receipt.Disposition, receipt.ReceiptHash)
        });
    }

    private static HumanReviewLifecycle CreateLifecycle(HumanReviewRunState state, HumanReviewRequestReference request, HumanReviewDecisionReference? decision, HumanReviewDecisionKind? kind, HumanReviewDecisionOperationDisposition disposition, string correlationId, DateTimeOffset atUtc)
    {
        var status = disposition == HumanReviewDecisionOperationDisposition.Expired
            ? HumanReviewLifecycleStatus.Expired
            : kind switch
            {
                HumanReviewDecisionKind.Approve => HumanReviewLifecycleStatus.Approved,
                HumanReviewDecisionKind.Reject => HumanReviewLifecycleStatus.Rejected,
                HumanReviewDecisionKind.Cancel => HumanReviewLifecycleStatus.Cancelled,
                HumanReviewDecisionKind.RequestInformation => HumanReviewLifecycleStatus.AwaitingInformation,
                _ => HumanReviewLifecycleStatus.Unknown,
            };
        var head = state.LifecycleHistory[^1];
        return HumanReviewContractHash.ApplyLifecycle(new HumanReviewLifecycle(1, request, status, head.LifecycleVersion + 1, atUtc, decision, ServerProvenance(correlationId, atUtc), head.LifecycleHash, string.Empty));
    }

    private static HumanReviewDecisionReference Reference(HumanReviewDecision decision) => new(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash);

    private static HumanReviewProvenance ServerProvenance(string correlationId, DateTimeOffset atUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-decision", correlationId, atUtc, string.Empty));

    private static CustomLoopRunEvent CreateOperationEvent(CustomLoopRunRecord run, HumanReviewEvidence evidence, HumanReviewDecisionOperationReceipt receipt)
        => new(run.Events.LongLength + 1, Id("event", evidence.EvidenceId), evidence.RecordedAtUtc, CustomLoopRunEventKind.HumanReviewDecisionOperationRecorded, null, null, null, "Human Review decision operation was durably recorded.", [], null, null, null, null, null, null, null, null, null, null, null)
        {
            HumanReviewEvidence = evidence,
            HumanReviewDecisionOperation = new HumanReviewDecisionOperationReference(receipt.DecisionOperationId, receipt.ProposalHash, receipt.Disposition, receipt.ReceiptHash)
        };

    private static CustomLoopRunEvent CreateReservationEvent(CustomLoopRunRecord run, HumanReviewEvidence evidence)
        => new(run.Events.LongLength + 2, Id("event", evidence.EvidenceId), evidence.RecordedAtUtc, CustomLoopRunEventKind.HumanReviewContinuationReserved, null, null, null, "Human Review approval continuation was reserved without release.", [], null, null, null, null, null, null, null, null, null, null, null)
        {
            HumanReviewEvidence = evidence,
            HumanReviewContinuationReservation = evidence.ContinuationReservation
        };

    private static bool IsCanonicalSuccessor(CustomLoopRunRecord current, CustomLoopRunRecord next)
    {
        try
        {
            return CustomLoopRunValidator.ValidateUpdate(current, next).IsValid;
        }
        catch
        {
            return false;
        }
    }

    private async Task<HumanReviewDecisionServiceResult?> TryRecoverExactOperationAsync(string runId, string requestHash, HumanReviewDecisionProposal proposal, bool permitted)
    {
        try
        {
            var durable = await _runs.GetAsync(runId, CancellationToken.None);
            if (durable is null || !IsCanonicalReviewBlockedRun(durable, out var state) || !string.Equals(state.Request.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return null;
            }

            var receipt = state.OperationReceipts.SingleOrDefault(item => string.Equals(item.DecisionOperationId, proposal.DecisionOperationId, StringComparison.Ordinal));
            if (receipt is null || !string.Equals(receipt.ProposalHash, proposal.ProposalHash, StringComparison.Ordinal))
            {
                return null;
            }

            return permitted
                ? Result(HumanReviewDecisionServiceStatus.Replayed, CopyReceipt(receipt))
                : Result(HumanReviewDecisionServiceStatus.Denied);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetExactCommittedReceipt(CustomLoopRunRecord expectedCandidate, CustomLoopRunRecord? committed, HumanReviewDecisionOperationReceipt expected, out HumanReviewDecisionOperationReceipt receipt)
    {
        receipt = null!;
        try
        {
            if (committed is null || !CustomLoopRunValidator.HasSameDurableVersion(expectedCandidate, committed))
            {
                return false;
            }

            var durable = committed.HumanReview?.OperationReceipts.SingleOrDefault(item => string.Equals(item.DecisionOperationId, expected.DecisionOperationId, StringComparison.Ordinal));
            if (durable is null || !string.Equals(durable.ReceiptHash, expected.ReceiptHash, StringComparison.Ordinal))
            {
                return false;
            }

            receipt = CopyReceipt(durable);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HumanReviewDecisionOperationReceipt CopyReceipt(HumanReviewDecisionOperationReceipt receipt)
        => receipt with
        {
            Request = receipt.Request with { },
            Decision = receipt.Decision is null ? null : receipt.Decision with { },
            Provenance = receipt.Provenance with { }
        };

    private static HumanReviewDecisionServiceStatus StatusFor(HumanReviewDecisionOperationDisposition disposition)
        => disposition switch
        {
            HumanReviewDecisionOperationDisposition.Accepted => HumanReviewDecisionServiceStatus.Accepted,
            HumanReviewDecisionOperationDisposition.InformationRequested => HumanReviewDecisionServiceStatus.InformationRequested,
            HumanReviewDecisionOperationDisposition.Denied => HumanReviewDecisionServiceStatus.Denied,
            HumanReviewDecisionOperationDisposition.Conflict => HumanReviewDecisionServiceStatus.Conflict,
            HumanReviewDecisionOperationDisposition.Expired => HumanReviewDecisionServiceStatus.Expired,
            _ => HumanReviewDecisionServiceStatus.Invalid,
        };

    private static HumanReviewDecisionServiceResult Result(HumanReviewDecisionServiceStatus status, HumanReviewDecisionOperationReceipt? receipt = null) => new(status, receipt);

    private static string Id(string prefix, string value) => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
