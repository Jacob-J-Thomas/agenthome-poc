using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines one immutable, authenticated, client-idempotent Human Review decision attempt; later services decide eligibility and continuation without trusting caller claims.</summary>
/// <param name="SchemaVersion">The decision schema version, which must be 1.</param>
/// <param name="DecisionId">The globally unique decision identity.</param>
/// <param name="DecisionOperationId">The client-supplied globally unique decision operation identity.</param>
/// <param name="Request">The exact immutable request reference.</param>
/// <param name="Kind">The closed requested decision kind.</param>
/// <param name="AuthenticatedActorId">The harness-authenticated actor identity, never a caller-asserted display name.</param>
/// <param name="ReviewerRoleId">The exact currently authenticated reviewer role presented for validation.</param>
/// <param name="ReviewerScopeIds">The canonical ordered reviewer scope set presented for validation.</param>
/// <param name="DecidedAtUtc">The trusted UTC decision timestamp.</param>
/// <param name="Detail">Optional bounded untrusted redacted detail; it is required for <see cref="HumanReviewDecisionKind.RequestInformation"/> and cannot alter executable inputs.</param>
/// <param name="Provenance">The immutable authenticated-reviewer provenance.</param>
/// <param name="DecisionHash">The canonical hash of every behavior-affecting decision field.</param>
public sealed partial record HumanReviewDecision(
    int SchemaVersion,
    string DecisionId,
    string DecisionOperationId,
    HumanReviewRequestReference Request,
    HumanReviewDecisionKind Kind,
    string AuthenticatedActorId,
    string ReviewerRoleId,
    ImmutableArray<string> ReviewerScopeIds,
    DateTimeOffset DecidedAtUtc,
    string? Detail,
    HumanReviewProvenance Provenance,
    string DecisionHash)
{
    /// <summary>Gets the only supported decision schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;

}
