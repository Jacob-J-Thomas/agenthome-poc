using System.Globalization;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Computes, applies, and verifies canonical lowercase SHA-256 hashes for immutable schema-1 Human Review contracts.</summary>
public static class HumanReviewContractHash
{
    /// <summary>Computes the canonical hash of an effect-attempt binding excluding its self-referential hash field.</summary>
    /// <param name="effectAttempt">The effect-attempt binding.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputeEffectAttempt(HumanReviewEffectAttemptBinding effectAttempt)
    {
        ArgumentNullException.ThrowIfNull(effectAttempt);
        return Compute("human-review-effect-attempt-v1", canonical => AppendEffectAttempt(canonical, effectAttempt, includeHash: false));
    }

    /// <summary>Returns an effect-attempt binding with its canonical hash applied.</summary>
    /// <param name="effectAttempt">The effect-attempt binding candidate.</param>
    /// <returns>A copy carrying the exact canonical hash.</returns>
    public static HumanReviewEffectAttemptBinding ApplyEffectAttempt(HumanReviewEffectAttemptBinding effectAttempt)
    {
        ArgumentNullException.ThrowIfNull(effectAttempt);
        return effectAttempt with { EffectAttemptHash = ComputeEffectAttempt(effectAttempt) };
    }

    /// <summary>Gets whether an effect-attempt binding retains its exact canonical hash.</summary>
    /// <param name="effectAttempt">The effect-attempt binding to inspect.</param>
    /// <returns><see langword="true"/> when the hash is canonical and exact.</returns>
    public static bool MatchesEffectAttempt(HumanReviewEffectAttemptBinding? effectAttempt)
        => effectAttempt is not null && IsSha256(effectAttempt.EffectAttemptHash) && FixedEquals(ComputeEffectAttempt(effectAttempt), effectAttempt.EffectAttemptHash);

    /// <summary>Computes the canonical hash of an exact Human Review binding excluding its self-referential hash field.</summary>
    /// <param name="binding">The exact binding.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputeBinding(HumanReviewBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return Compute("human-review-binding-v1", canonical => AppendBinding(canonical, binding, includeHash: false));
    }

    /// <summary>Returns an exact binding copy with nested effect and canonical binding hashes applied.</summary>
    /// <param name="binding">The binding candidate.</param>
    /// <returns>A copy carrying all canonical binding hashes.</returns>
    public static HumanReviewBinding ApplyBinding(HumanReviewBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var withEffectHash = binding with { EffectAttempt = binding.EffectAttempt is null ? null : ApplyEffectAttempt(binding.EffectAttempt) };
        return withEffectHash with { BindingHash = ComputeBinding(withEffectHash) };
    }

    /// <summary>Gets whether a binding retains its exact canonical hash and, when present, exact effect-attempt hash.</summary>
    /// <param name="binding">The binding to inspect.</param>
    /// <returns><see langword="true"/> when all retained hashes are canonical and exact.</returns>
    public static bool MatchesBinding(HumanReviewBinding? binding)
        => binding is not null
            && IsSha256(binding.BindingHash)
            && (binding.EffectAttempt is null || MatchesEffectAttempt(binding.EffectAttempt))
            && FixedEquals(ComputeBinding(binding), binding.BindingHash);

    /// <summary>Computes the canonical hash of retained admission binding evidence excluding its self-referential hash field.</summary>
    /// <param name="evidence">The immutable admission binding evidence.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputeAdmissionBindingEvidence(HumanReviewAdmissionBindingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Compute("human-review-admission-binding-evidence-v1", canonical => AppendAdmissionBindingEvidence(canonical, evidence, includeHash: false));
    }

    /// <summary>Returns admission binding evidence with its canonical nested binding and evidence hashes applied.</summary>
    /// <param name="evidence">The admission binding evidence candidate.</param>
    /// <returns>A copy carrying canonical hashes.</returns>
    public static HumanReviewAdmissionBindingEvidence ApplyAdmissionBindingEvidence(HumanReviewAdmissionBindingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var withEffectHash = evidence with { EffectAttempt = evidence.EffectAttempt is null ? null : ApplyEffectAttempt(evidence.EffectAttempt) };
        return withEffectHash with { EvidenceHash = ComputeAdmissionBindingEvidence(withEffectHash) };
    }

    /// <summary>Gets whether retained admission binding evidence is complete and hash-valid.</summary>
    /// <param name="evidence">The admission binding evidence to inspect.</param>
    /// <returns><see langword="true"/> only when every retained field is canonical.</returns>
    public static bool MatchesAdmissionBindingEvidence(HumanReviewAdmissionBindingEvidence? evidence)
        => evidence is not null
            && evidence.SchemaVersion == HumanReviewAdmissionBindingEvidence.CurrentSchemaVersion
            && IsSha256(evidence.BindingHash)
            && !string.IsNullOrWhiteSpace(evidence.FrontierId)
            && evidence.FrontierVersion is > 0 and <= HumanReviewContractLimits.MaxVersion
            && IsSha256(evidence.FrontierHash)
            && (evidence.EffectAttempt is null || MatchesEffectAttempt(evidence.EffectAttempt))
            && evidence.ExecutionGeneration is > 0 and <= HumanReviewContractLimits.MaxVersion
            && IsSha256(evidence.EvidenceHash)
            && FixedEquals(ComputeAdmissionBindingEvidence(evidence), evidence.EvidenceHash);

    /// <summary>Computes the canonical hash of an exact approval scope excluding its self-referential hash field.</summary>
    /// <param name="scope">The approval scope.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputeApprovalScope(HumanReviewApprovalScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Compute("human-review-approval-scope-v1", canonical => AppendApprovalScope(canonical, scope, includeHash: false));
    }

    /// <summary>Returns an approval scope copy with its canonical hash applied.</summary>
    /// <param name="scope">The approval-scope candidate.</param>
    /// <returns>A copy carrying the exact canonical hash.</returns>
    public static HumanReviewApprovalScope ApplyApprovalScope(HumanReviewApprovalScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope with { ScopeHash = ComputeApprovalScope(scope) };
    }

    /// <summary>Gets whether an approval scope retains its exact canonical hash.</summary>
    /// <param name="scope">The approval scope to inspect.</param>
    /// <returns><see langword="true"/> when the hash is canonical and exact.</returns>
    public static bool MatchesApprovalScope(HumanReviewApprovalScope? scope)
        => scope is not null && IsSha256(scope.ScopeHash) && FixedEquals(ComputeApprovalScope(scope), scope.ScopeHash);

    /// <summary>Computes the canonical hash of a redacted preview excluding its self-referential hash field.</summary>
    /// <param name="preview">The redacted preview.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputePreview(HumanReviewRedactedPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return Compute("human-review-preview-v1", canonical => AppendPreview(canonical, preview, includeHash: false));
    }

    /// <summary>Returns a redacted preview copy with its canonical hash applied.</summary>
    /// <param name="preview">The preview candidate.</param>
    /// <returns>A copy carrying the exact canonical hash.</returns>
    public static HumanReviewRedactedPreview ApplyPreview(HumanReviewRedactedPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return preview with { DetailHash = ComputePreview(preview) };
    }

    /// <summary>Gets whether a redacted preview retains its exact canonical hash.</summary>
    /// <param name="preview">The preview to inspect.</param>
    /// <returns><see langword="true"/> when the hash is canonical and exact.</returns>
    public static bool MatchesPreview(HumanReviewRedactedPreview? preview)
        => preview is not null && IsSha256(preview.DetailHash) && FixedEquals(ComputePreview(preview), preview.DetailHash);

    /// <summary>Computes the canonical hash of immutable provenance excluding its self-referential hash field.</summary>
    /// <param name="provenance">The provenance value.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputeProvenance(HumanReviewProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return Compute("human-review-provenance-v1", canonical => AppendProvenance(canonical, provenance, includeHash: false));
    }

    /// <summary>Returns a provenance copy with its canonical hash applied.</summary>
    /// <param name="provenance">The provenance candidate.</param>
    /// <returns>A copy carrying the exact canonical hash.</returns>
    public static HumanReviewProvenance ApplyProvenance(HumanReviewProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return provenance with { ProvenanceHash = ComputeProvenance(provenance) };
    }

    /// <summary>Gets whether provenance retains its exact canonical hash.</summary>
    /// <param name="provenance">The provenance to inspect.</param>
    /// <returns><see langword="true"/> when the hash is canonical and exact.</returns>
    public static bool MatchesProvenance(HumanReviewProvenance? provenance)
        => provenance is not null && IsSha256(provenance.ProvenanceHash) && FixedEquals(ComputeProvenance(provenance), provenance.ProvenanceHash);

    /// <summary>Computes the canonical hash of a request excluding its self-referential hash field.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputeRequest(HumanReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Compute("human-review-request-v1", canonical => AppendRequest(canonical, request, includeHash: false));
    }

    /// <summary>Returns a request copy with all nested canonical hashes and its request hash applied.</summary>
    /// <param name="request">The request candidate.</param>
    /// <returns>A copy carrying canonical hashes.</returns>
    public static HumanReviewRequest ApplyRequest(HumanReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var withNestedHashes = request with
        {
            Binding = ApplyBinding(request.Binding),
            ApprovalScope = ApplyApprovalScope(request.ApprovalScope),
            Previews = request.Previews.IsDefault ? default : request.Previews.Select(ApplyPreview).ToImmutableArray(),
            Provenance = ApplyProvenance(request.Provenance)
        };
        return withNestedHashes with { RequestHash = ComputeRequest(withNestedHashes) };
    }

    /// <summary>Gets whether a request retains its exact canonical hash and required nested hashes.</summary>
    /// <param name="request">The request to inspect.</param>
    /// <returns><see langword="true"/> when all retained hashes are canonical and exact.</returns>
    public static bool MatchesRequest(HumanReviewRequest? request)
        => request is not null
            && IsSha256(request.RequestHash)
            && MatchesBinding(request.Binding)
            && MatchesApprovalScope(request.ApprovalScope)
            && !request.Previews.IsDefault
            && request.Previews.All(MatchesPreview)
            && request.Timing is not null
            && MatchesProvenance(request.Provenance)
            && FixedEquals(ComputeRequest(request), request.RequestHash);

    /// <summary>Computes the canonical hash of a reviewer decision excluding its self-referential hash field.</summary>
    /// <param name="decision">The decision.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputeDecision(HumanReviewDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return Compute("human-review-decision-v1", canonical => AppendDecision(canonical, decision, includeHash: false));
    }

    /// <summary>Returns a decision copy with nested provenance and canonical decision hashes applied.</summary>
    /// <param name="decision">The decision candidate.</param>
    /// <returns>A copy carrying canonical hashes.</returns>
    public static HumanReviewDecision ApplyDecision(HumanReviewDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var withProvenance = decision with { Provenance = ApplyProvenance(decision.Provenance) };
        return withProvenance with { DecisionHash = ComputeDecision(withProvenance) };
    }

    /// <summary>Gets whether a decision retains its exact canonical hash and provenance hash.</summary>
    /// <param name="decision">The decision to inspect.</param>
    /// <returns><see langword="true"/> when all retained hashes are canonical and exact.</returns>
    public static bool MatchesDecision(HumanReviewDecision? decision)
        => decision is not null && IsSha256(decision.DecisionHash) && MatchesProvenance(decision.Provenance) && FixedEquals(ComputeDecision(decision), decision.DecisionHash);

    /// <summary>Computes the canonical hash of a lifecycle head excluding its self-referential hash field.</summary>
    /// <param name="lifecycle">The lifecycle head.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputeLifecycle(HumanReviewLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        return Compute("human-review-lifecycle-v1", canonical => AppendLifecycle(canonical, lifecycle, includeHash: false));
    }

    /// <summary>Returns a lifecycle copy with nested provenance and canonical lifecycle hashes applied.</summary>
    /// <param name="lifecycle">The lifecycle candidate.</param>
    /// <returns>A copy carrying canonical hashes.</returns>
    public static HumanReviewLifecycle ApplyLifecycle(HumanReviewLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        var withProvenance = lifecycle with { Provenance = ApplyProvenance(lifecycle.Provenance) };
        return withProvenance with { LifecycleHash = ComputeLifecycle(withProvenance) };
    }

    /// <summary>Gets whether a lifecycle head retains its exact canonical hash and provenance hash.</summary>
    /// <param name="lifecycle">The lifecycle head to inspect.</param>
    /// <returns><see langword="true"/> when all retained hashes are canonical and exact.</returns>
    public static bool MatchesLifecycle(HumanReviewLifecycle? lifecycle)
        => lifecycle is not null && IsSha256(lifecycle.LifecycleHash) && MatchesProvenance(lifecycle.Provenance) && FixedEquals(ComputeLifecycle(lifecycle), lifecycle.LifecycleHash);

    /// <summary>Computes the canonical hash of append-only evidence excluding its self-referential hash field.</summary>
    /// <param name="evidence">The evidence artifact.</param>
    /// <returns>The canonical lowercase SHA-256 hash.</returns>
    public static string ComputeEvidence(HumanReviewEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Compute("human-review-evidence-v1", canonical => AppendEvidence(canonical, evidence, includeHash: false));
    }

    /// <summary>Returns evidence with nested canonical hashes and its evidence hash applied.</summary>
    /// <param name="evidence">The evidence candidate.</param>
    /// <returns>A copy carrying canonical hashes.</returns>
    public static HumanReviewEvidence ApplyEvidence(HumanReviewEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var withNestedHashes = evidence with
        {
            Provenance = ApplyProvenance(evidence.Provenance),
            Previews = evidence.Previews.IsDefault ? default : evidence.Previews.Select(ApplyPreview).ToImmutableArray()
        };
        return withNestedHashes with { EvidenceHash = ComputeEvidence(withNestedHashes) };
    }

    /// <summary>Gets whether evidence retains its exact canonical hash and nested hashes.</summary>
    /// <param name="evidence">The evidence artifact to inspect.</param>
    /// <returns><see langword="true"/> when all retained hashes are canonical and exact.</returns>
    public static bool MatchesEvidence(HumanReviewEvidence? evidence)
        => evidence is not null
            && IsSha256(evidence.EvidenceHash)
            && MatchesProvenance(evidence.Provenance)
            && !evidence.Previews.IsDefault
            && evidence.Previews.All(MatchesPreview)
            && FixedEquals(ComputeEvidence(evidence), evidence.EvidenceHash);

    /// <summary>Computes the canonical hash of a bounded decision proposal.</summary>
    public static string ComputeDecisionProposal(HumanReviewDecisionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return Compute("human-review-decision-proposal-v1", canonical => AppendDecisionProposal(canonical, proposal, includeHash: false));
    }

    /// <summary>Returns a proposal with its server-derived canonical hash applied.</summary>
    public static HumanReviewDecisionProposal ApplyDecisionProposal(HumanReviewDecisionProposal proposal) => proposal with { ProposalHash = ComputeDecisionProposal(proposal) };

    /// <summary>Gets whether a proposal retains its exact canonical hash.</summary>
    public static bool MatchesDecisionProposal(HumanReviewDecisionProposal? proposal)
        => proposal is not null && IsSha256(proposal.ProposalHash) && FixedEquals(ComputeDecisionProposal(proposal), proposal.ProposalHash);

    /// <summary>Computes the canonical hash of one decision-operation receipt.</summary>
    public static string ComputeDecisionOperationReceipt(HumanReviewDecisionOperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return Compute("human-review-decision-operation-receipt-v1", canonical => AppendDecisionOperationReceipt(canonical, receipt, includeHash: false));
    }

    /// <summary>Returns a receipt with nested provenance and its canonical hash applied.</summary>
    public static HumanReviewDecisionOperationReceipt ApplyDecisionOperationReceipt(HumanReviewDecisionOperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var prepared = receipt with { Provenance = ApplyProvenance(receipt.Provenance) };
        return prepared with { ReceiptHash = ComputeDecisionOperationReceipt(prepared) };
    }

    /// <summary>Gets whether a receipt retains its exact canonical hash.</summary>
    public static bool MatchesDecisionOperationReceipt(HumanReviewDecisionOperationReceipt? receipt)
        => receipt is not null && IsSha256(receipt.ReceiptHash) && MatchesProvenance(receipt.Provenance) && FixedEquals(ComputeDecisionOperationReceipt(receipt), receipt.ReceiptHash);

    /// <summary>Computes the canonical hash of one approval continuation reservation.</summary>
    public static string ComputeContinuationReservation(HumanReviewContinuationReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        return Compute("human-review-continuation-reservation-v1", canonical => AppendContinuationReservation(canonical, reservation, includeHash: false));
    }

    /// <summary>Returns a reservation with nested provenance and its canonical hash applied.</summary>
    public static HumanReviewContinuationReservation ApplyContinuationReservation(HumanReviewContinuationReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var prepared = reservation with { Provenance = ApplyProvenance(reservation.Provenance) };
        return prepared with { ReservationHash = ComputeContinuationReservation(prepared) };
    }

    /// <summary>Gets whether a reservation retains its exact canonical hash.</summary>
    public static bool MatchesContinuationReservation(HumanReviewContinuationReservation? reservation)
        => reservation is not null && IsSha256(reservation.ReservationHash) && MatchesProvenance(reservation.Provenance) && FixedEquals(ComputeContinuationReservation(reservation), reservation.ReservationHash);

    /// <summary>Determines whether a string is a canonical lowercase SHA-256 digest.</summary>
    /// <param name="value">The candidate digest.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is exactly 64 lowercase hexadecimal characters.</returns>
    public static bool IsSha256(string? value) => value is { Length: HumanReviewContractLimits.Sha256HexCharacters } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Compute(string domain, Action<StringBuilder> append)
    {
        var canonical = new StringBuilder(2_048);
        Append(canonical, domain);
        append(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static void AppendRequest(StringBuilder canonical, HumanReviewRequest request, bool includeHash)
    {
        Append(canonical, request.SchemaVersion);
        Append(canonical, request.RequestId);
        Append(canonical, request.RequestOperationId);
        AppendBinding(canonical, request.Binding, includeHash: true);
        Append(canonical, (int)request.Purpose);
        AppendDecisionKinds(canonical, request.RequestedDecisions);
        AppendReviewerScopes(canonical, request.EligibleReviewers);
        AppendApprovalScope(canonical, request.ApprovalScope, includeHash: true);
        AppendPreviews(canonical, request.Previews);
        AppendTiming(canonical, request.Timing);
        AppendProvenance(canonical, request.Provenance, includeHash: true);
        if (includeHash)
        {
            Append(canonical, request.RequestHash);
        }
    }

    private static void AppendDecision(StringBuilder canonical, HumanReviewDecision decision, bool includeHash)
    {
        Append(canonical, decision.SchemaVersion);
        Append(canonical, decision.DecisionId);
        Append(canonical, decision.DecisionOperationId);
        AppendRequestReference(canonical, decision.Request);
        Append(canonical, (int)decision.Kind);
        Append(canonical, decision.AuthenticatedActorId);
        Append(canonical, decision.ReviewerRoleId);
        AppendIdentifiers(canonical, decision.ReviewerScopeIds);
        Append(canonical, decision.DecidedAtUtc);
        Append(canonical, decision.Detail);
        AppendProvenance(canonical, decision.Provenance, includeHash: true);
        if (includeHash)
        {
            Append(canonical, decision.DecisionHash);
        }
    }

    private static void AppendLifecycle(StringBuilder canonical, HumanReviewLifecycle lifecycle, bool includeHash)
    {
        Append(canonical, lifecycle.SchemaVersion);
        AppendRequestReference(canonical, lifecycle.Request);
        Append(canonical, (int)lifecycle.Status);
        Append(canonical, lifecycle.LifecycleVersion);
        Append(canonical, lifecycle.UpdatedAtUtc);
        AppendDecisionReference(canonical, lifecycle.LastDecision);
        AppendProvenance(canonical, lifecycle.Provenance, includeHash: true);
        Append(canonical, lifecycle.PreviousLifecycleHash);
        if (includeHash)
        {
            Append(canonical, lifecycle.LifecycleHash);
        }
    }

    private static void AppendEvidence(StringBuilder canonical, HumanReviewEvidence evidence, bool includeHash)
    {
        Append(canonical, evidence.SchemaVersion);
        Append(canonical, evidence.EvidenceId);
        AppendRequestReference(canonical, evidence.Request);
        Append(canonical, (int)evidence.Kind);
        AppendDecisionReference(canonical, evidence.Decision);
        Append(canonical, evidence.RecordedAtUtc);
        AppendProvenance(canonical, evidence.Provenance, includeHash: true);
        AppendPreviews(canonical, evidence.Previews);
        Append(canonical, evidence.PreviousEvidenceHash);
        // Preserve the exact #544 admission-evidence hash domain when both later state-plane references are absent.
        // Decision/reservation evidence is unambiguous because validation requires its applicable typed reference.
        if (evidence.DecisionOperation is not null || evidence.ContinuationReservation is not null)
        {
            AppendDecisionOperationReference(canonical, evidence.DecisionOperation);
            AppendContinuationReservationReference(canonical, evidence.ContinuationReservation);
        }
        if (includeHash)
        {
            Append(canonical, evidence.EvidenceHash);
        }
    }

    private static void AppendDecisionProposal(StringBuilder canonical, HumanReviewDecisionProposal proposal, bool includeHash)
    {
        Append(canonical, proposal.SchemaVersion);
        Append(canonical, proposal.DecisionOperationId);
        Append(canonical, (int)proposal.Kind);
        Append(canonical, proposal.Detail);
        if (includeHash) Append(canonical, proposal.ProposalHash);
    }

    private static void AppendDecisionOperationReceipt(StringBuilder canonical, HumanReviewDecisionOperationReceipt receipt, bool includeHash)
    {
        Append(canonical, receipt.SchemaVersion);
        Append(canonical, receipt.DecisionOperationId);
        Append(canonical, receipt.ProposalHash);
        AppendRequestReference(canonical, receipt.Request);
        Append(canonical, (int)receipt.Disposition);
        AppendDecisionReference(canonical, receipt.Decision);
        Append(canonical, receipt.RecordedAtUtc);
        AppendProvenance(canonical, receipt.Provenance, includeHash: true);
        if (includeHash) Append(canonical, receipt.ReceiptHash);
    }

    private static void AppendContinuationReservation(StringBuilder canonical, HumanReviewContinuationReservation reservation, bool includeHash)
    {
        Append(canonical, reservation.SchemaVersion);
        Append(canonical, reservation.ReservationId);
        AppendRequestReference(canonical, reservation.Request);
        AppendDecisionReference(canonical, reservation.Decision);
        Append(canonical, reservation.ReservedAtUtc);
        AppendProvenance(canonical, reservation.Provenance, includeHash: true);
        if (includeHash) Append(canonical, reservation.ReservationHash);
    }

    private static void AppendBinding(StringBuilder canonical, HumanReviewBinding binding, bool includeHash)
    {
        Append(canonical, binding.SchemaVersion);
        Append(canonical, binding.WorkspaceId);
        Append(canonical, binding.RunId);
        Append(canonical, binding.GraphId);
        Append(canonical, binding.RevisionId);
        Append(canonical, binding.RevisionHash);
        Append(canonical, binding.NodeId);
        AppendNullable(canonical, binding.ActivationOrdinal);
        AppendNullable(canonical, binding.VisitOrdinal);
        Append(canonical, binding.Attempt);
        Append(canonical, binding.FrontierId);
        Append(canonical, binding.FrontierVersion);
        Append(canonical, binding.FrontierHash);
        Append(canonical, binding.AuthorityProfileHash);
        Append(canonical, binding.AuthorityGrantHash);
        Append(canonical, binding.CapabilityHash);
        Append(canonical, binding.ModelProfileHash);
        Append(canonical, binding.TargetHash);
        Append(canonical, binding.PreconditionHash);
        Append(canonical, binding.PayloadHash);
        AppendEffectAttempt(canonical, binding.EffectAttempt, includeHash: true);
        if (includeHash)
        {
            Append(canonical, binding.BindingHash);
        }
    }

    private static void AppendAdmissionBindingEvidence(StringBuilder canonical, HumanReviewAdmissionBindingEvidence evidence, bool includeHash)
    {
        Append(canonical, evidence.SchemaVersion);
        Append(canonical, evidence.BindingHash);
        Append(canonical, evidence.FrontierId);
        Append(canonical, evidence.FrontierVersion);
        Append(canonical, evidence.FrontierHash);
        AppendEffectAttempt(canonical, evidence.EffectAttempt, includeHash: true);
        Append(canonical, evidence.ExecutionGeneration);
        if (includeHash)
        {
            Append(canonical, evidence.EvidenceHash);
        }
    }

    private static void AppendEffectAttempt(StringBuilder canonical, HumanReviewEffectAttemptBinding? effectAttempt, bool includeHash)
    {
        if (effectAttempt is null)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, effectAttempt.EffectAttemptId);
        Append(canonical, effectAttempt.OperationId);
        Append(canonical, effectAttempt.EffectGeneration);
        Append(canonical, effectAttempt.IntentHash);
        Append(canonical, effectAttempt.PreparationHash);
        Append(canonical, (int)effectAttempt.DispatchCertainty);
        if (includeHash)
        {
            Append(canonical, effectAttempt.EffectAttemptHash);
        }
    }

    private static void AppendApprovalScope(StringBuilder canonical, HumanReviewApprovalScope scope, bool includeHash)
    {
        Append(canonical, (int)scope.Kind);
        Append(canonical, scope.BindingHash);
        Append(canonical, scope.EffectAttemptId);
        if (includeHash)
        {
            Append(canonical, scope.ScopeHash);
        }
    }

    private static void AppendPreviews(StringBuilder canonical, ImmutableArray<HumanReviewRedactedPreview> previews)
    {
        if (previews.IsDefault)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, previews.Length);
        foreach (var preview in previews)
        {
            AppendPreview(canonical, preview, includeHash: true);
        }
    }

    private static void AppendPreview(StringBuilder canonical, HumanReviewRedactedPreview? preview, bool includeHash)
    {
        if (preview is null)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, (int)preview.Kind);
        Append(canonical, preview.Label);
        Append(canonical, preview.Detail);
        if (includeHash)
        {
            Append(canonical, preview.DetailHash);
        }
    }

    private static void AppendTiming(StringBuilder canonical, HumanReviewTiming timing)
    {
        Append(canonical, timing.CreatedAtUtc);
        Append(canonical, timing.DueAtUtc);
        Append(canonical, timing.ExpiresAtUtc);
    }

    private static void AppendProvenance(StringBuilder canonical, HumanReviewProvenance provenance, bool includeHash)
    {
        Append(canonical, (int)provenance.Kind);
        Append(canonical, provenance.SourceId);
        Append(canonical, provenance.CorrelationId);
        Append(canonical, provenance.ObservedAtUtc);
        if (includeHash)
        {
            Append(canonical, provenance.ProvenanceHash);
        }
    }

    private static void AppendRequestReference(StringBuilder canonical, HumanReviewRequestReference? reference)
    {
        if (reference is null)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, reference.RequestId);
        Append(canonical, reference.RequestHash);
    }

    private static void AppendDecisionReference(StringBuilder canonical, HumanReviewDecisionReference? reference)
    {
        if (reference is null)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, reference.DecisionId);
        Append(canonical, reference.DecisionOperationId);
        Append(canonical, (int)reference.Kind);
        Append(canonical, reference.DecisionHash);
    }

    private static void AppendDecisionOperationReference(StringBuilder canonical, HumanReviewDecisionOperationReference? reference)
    {
        if (reference is null) { Append(canonical, null); return; }
        Append(canonical, reference.DecisionOperationId);
        Append(canonical, reference.ProposalHash);
        Append(canonical, (int)reference.Disposition);
        Append(canonical, reference.ReceiptHash);
    }

    private static void AppendContinuationReservationReference(StringBuilder canonical, HumanReviewContinuationReservationReference? reference)
    {
        if (reference is null) { Append(canonical, null); return; }
        Append(canonical, reference.ReservationId);
        Append(canonical, reference.ReservationHash);
    }

    private static void AppendDecisionKinds(StringBuilder canonical, ImmutableArray<HumanReviewDecisionKind> values)
    {
        if (values.IsDefault)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, values.Length);
        foreach (var value in values)
        {
            Append(canonical, (int)value);
        }
    }

    private static void AppendReviewerScopes(StringBuilder canonical, ImmutableArray<HumanReviewReviewerScope> reviewers)
    {
        if (reviewers.IsDefault)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, reviewers.Length);
        foreach (var reviewer in reviewers)
        {
            if (reviewer is null)
            {
                Append(canonical, null);
                continue;
            }

            Append(canonical, reviewer.ReviewerRoleId);
            AppendIdentifiers(canonical, reviewer.ScopeIds);
        }
    }

    private static void AppendIdentifiers(StringBuilder canonical, ImmutableArray<string> values)
    {
        if (values.IsDefault)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, values.Length);
        foreach (var value in values)
        {
            Append(canonical, value);
        }
    }

    private static void Append(StringBuilder canonical, DateTimeOffset value) => Append(canonical, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, int value) => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, long value) => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void AppendNullable(StringBuilder canonical, int? value)
    {
        if (value is null)
        {
            Append(canonical, null);
            return;
        }

        Append(canonical, value.Value);
    }

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }

        var normalized = value.Normalize(NormalizationForm.FormC);
        canonical.Append(Encoding.UTF8.GetByteCount(normalized).ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(normalized);
    }
}
