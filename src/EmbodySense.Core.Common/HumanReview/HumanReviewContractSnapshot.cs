using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Creates bounded deep copies of untrusted Human Review contract artifacts before durable use.</summary>
public static class HumanReviewContractSnapshot
{
    /// <summary>Captures and validates an independent immutable review-request snapshot.</summary>
    /// <param name="request">The potentially caller-owned request candidate.</param>
    /// <param name="snapshot">The independent request snapshot when validation succeeds.</param>
    /// <param name="validation">The deterministic validation result for the captured request.</param>
    /// <returns><see langword="true"/> when a valid independent request snapshot was captured; otherwise, <see langword="false"/>.</returns>
    public static bool TryCaptureRequest(HumanReviewRequest? request, out HumanReviewRequest? snapshot, out HumanReviewContractValidationResult validation)
    {
        if (request is null)
        {
            snapshot = null;
            validation = HumanReviewContractValidator.ValidateRequest(null);
            return false;
        }

        try
        {
            snapshot = Copy(request);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = Invalid("request_snapshot_unstable", "The bounded request changed while its snapshot was captured.");
            return false;
        }

        validation = HumanReviewContractValidator.ValidateRequest(snapshot);
        if (validation.IsValid)
        {
            return true;
        }

        snapshot = null;
        return false;
    }

    /// <summary>Captures and validates an independent decision snapshot against one exact immutable request snapshot.</summary>
    /// <param name="request">The exact request candidate.</param>
    /// <param name="decision">The potentially caller-owned decision candidate.</param>
    /// <param name="snapshot">The independent decision snapshot when validation succeeds.</param>
    /// <param name="validation">The deterministic request-relative decision validation result.</param>
    /// <returns><see langword="true"/> when valid independent request and decision snapshots were captured; otherwise, <see langword="false"/>.</returns>
    public static bool TryCaptureDecision(HumanReviewRequest? request, HumanReviewDecision? decision, out HumanReviewDecision? snapshot, out HumanReviewContractValidationResult validation)
    {
        if (!TryCaptureRequest(request, out var requestSnapshot, out validation) || decision is null)
        {
            snapshot = null;
            if (decision is null && validation.IsValid)
            {
                validation = HumanReviewContractValidator.ValidateDecision(requestSnapshot, null);
            }

            return false;
        }

        try
        {
            snapshot = Copy(decision);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = Invalid("decision_snapshot_unstable", "The bounded decision changed while its snapshot was captured.");
            return false;
        }

        validation = HumanReviewContractValidator.ValidateDecision(requestSnapshot, snapshot);
        if (validation.IsValid)
        {
            return true;
        }

        snapshot = null;
        return false;
    }

    /// <summary>Captures and validates an independent lifecycle snapshot against one exact immutable request snapshot.</summary>
    /// <param name="request">The exact request candidate.</param>
    /// <param name="lifecycle">The potentially caller-owned lifecycle candidate.</param>
    /// <param name="snapshot">The independent lifecycle snapshot when validation succeeds.</param>
    /// <param name="validation">The deterministic request-relative lifecycle validation result.</param>
    /// <returns><see langword="true"/> when valid independent request and lifecycle snapshots were captured; otherwise, <see langword="false"/>.</returns>
    public static bool TryCaptureLifecycle(HumanReviewRequest? request, HumanReviewLifecycle? lifecycle, out HumanReviewLifecycle? snapshot, out HumanReviewContractValidationResult validation)
    {
        if (!TryCaptureRequest(request, out var requestSnapshot, out validation) || lifecycle is null)
        {
            snapshot = null;
            if (lifecycle is null && validation.IsValid)
            {
                validation = HumanReviewContractValidator.ValidateLifecycle(requestSnapshot, null);
            }

            return false;
        }

        try
        {
            snapshot = Copy(lifecycle);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = Invalid("lifecycle_snapshot_unstable", "The bounded lifecycle changed or was malformed while its snapshot was captured.");
            return false;
        }

        validation = HumanReviewContractValidator.ValidateLifecycle(requestSnapshot, snapshot);
        if (validation.IsValid)
        {
            return true;
        }

        snapshot = null;
        return false;
    }

    /// <summary>Captures and validates an independent append-only evidence snapshot against one exact immutable request snapshot.</summary>
    /// <param name="request">The exact request candidate.</param>
    /// <param name="evidence">The potentially caller-owned evidence candidate.</param>
    /// <param name="snapshot">The independent evidence snapshot when validation succeeds.</param>
    /// <param name="validation">The deterministic request-relative evidence validation result.</param>
    /// <returns><see langword="true"/> when valid independent request and evidence snapshots were captured; otherwise, <see langword="false"/>.</returns>
    public static bool TryCaptureEvidence(HumanReviewRequest? request, HumanReviewEvidence? evidence, out HumanReviewEvidence? snapshot, out HumanReviewContractValidationResult validation)
    {
        if (!TryCaptureRequest(request, out var requestSnapshot, out validation) || evidence is null)
        {
            snapshot = null;
            if (evidence is null && validation.IsValid)
            {
                validation = HumanReviewContractValidator.ValidateEvidence(requestSnapshot, null);
            }

            return false;
        }

        try
        {
            snapshot = Copy(evidence);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = Invalid("evidence_snapshot_unstable", "The bounded evidence changed while its snapshot was captured.");
            return false;
        }

        validation = HumanReviewContractValidator.ValidateEvidence(requestSnapshot, snapshot);
        if (validation.IsValid)
        {
            return true;
        }

        snapshot = null;
        return false;
    }

    private static HumanReviewRequest Copy(HumanReviewRequest request)
        => request with
        {
            Binding = Copy(request.Binding),
            RequestedDecisions = Copy(request.RequestedDecisions),
            EligibleReviewers = Copy(request.EligibleReviewers),
            ApprovalScope = request.ApprovalScope is null ? null! : request.ApprovalScope with { },
            Previews = Copy(request.Previews),
            Timing = request.Timing is null ? null! : request.Timing with { },
            Provenance = request.Provenance is null ? null! : request.Provenance with { }
        };

    private static HumanReviewDecision Copy(HumanReviewDecision decision)
        => decision with
        {
            Request = decision.Request is null ? null! : decision.Request with { },
            ReviewerScopeIds = Copy(decision.ReviewerScopeIds),
            Provenance = decision.Provenance is null ? null! : decision.Provenance with { }
        };

    private static HumanReviewLifecycle Copy(HumanReviewLifecycle lifecycle)
        => lifecycle with
        {
            Request = lifecycle.Request is null ? null! : lifecycle.Request with { },
            LastDecision = lifecycle.LastDecision is null ? null : lifecycle.LastDecision with { },
            Provenance = lifecycle.Provenance is null ? null! : lifecycle.Provenance with { }
        };

    private static HumanReviewEvidence Copy(HumanReviewEvidence evidence)
        => evidence with
        {
            Request = evidence.Request is null ? null! : evidence.Request with { },
            Decision = evidence.Decision is null ? null : evidence.Decision with { },
            Provenance = evidence.Provenance is null ? null! : evidence.Provenance with { },
            Previews = Copy(evidence.Previews)
        };

    private static HumanReviewBinding Copy(HumanReviewBinding binding)
        => binding with { EffectAttempt = binding.EffectAttempt is null ? null : binding.EffectAttempt with { } };

    private static HumanReviewReviewerScope Copy(HumanReviewReviewerScope reviewer)
        => reviewer with { ScopeIds = Copy(reviewer.ScopeIds) };

    private static HumanReviewRedactedPreview Copy(HumanReviewRedactedPreview preview) => preview with { };

    private static ImmutableArray<HumanReviewDecisionKind> Copy(ImmutableArray<HumanReviewDecisionKind> values) => CopyValues(values);

    private static ImmutableArray<HumanReviewReviewerScope> Copy(ImmutableArray<HumanReviewReviewerScope> values) => values.IsDefault ? default : values.Select(Copy).ToImmutableArray();

    private static ImmutableArray<HumanReviewRedactedPreview> Copy(ImmutableArray<HumanReviewRedactedPreview> values) => values.IsDefault ? default : values.Select(Copy).ToImmutableArray();

    private static ImmutableArray<string> Copy(ImmutableArray<string> values) => CopyValues(values);

    private static ImmutableArray<T> CopyValues<T>(ImmutableArray<T> values)
        => values.IsDefault ? default : ImmutableCollectionsMarshal.AsImmutableArray(values.ToArray());

    private static HumanReviewContractValidationResult Invalid(string code, string message)
        => new([new HumanReviewContractValidationError(code, "$", message)]);
}
