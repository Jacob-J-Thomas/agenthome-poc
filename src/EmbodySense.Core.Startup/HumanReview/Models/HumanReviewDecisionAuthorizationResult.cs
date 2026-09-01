using System.Collections.Immutable;

namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Returns a server-owned authorization outcome bound to one exact Human Review evaluation.</summary>
/// <param name="Status">The closed server-owned authority outcome.</param>
/// <param name="RequestId">The exact canonical request identity echoed by the authority source.</param>
/// <param name="RequestHash">The exact canonical request hash echoed by the authority source.</param>
/// <param name="DecisionKind">The exact decision kind echoed by the authority source.</param>
/// <param name="DecisionOperationId">The exact operation identity echoed by the authority source.</param>
/// <param name="ProposalHash">The exact proposal hash echoed by the authority source.</param>
/// <param name="EvaluatedAtUtc">The exact trusted evaluation instant echoed by the authority source.</param>
/// <param name="ActorId">The server-resolved authenticated actor identity, when authorized.</param>
/// <param name="ReviewerRoleId">The server-resolved reviewer role, when authorized.</param>
/// <param name="ScopeIds">The server-resolved ordered reviewer scopes, when authorized.</param>
/// <param name="CorrelationId">The server-resolved authorization correlation identity, when authorized.</param>
public sealed record HumanReviewDecisionAuthorizationResult(
    HumanReviewDecisionAuthorizationStatus Status,
    string RequestId,
    string RequestHash,
    HumanReviewDecisionKind DecisionKind,
    string DecisionOperationId,
    string ProposalHash,
    DateTimeOffset EvaluatedAtUtc,
    string? ActorId,
    string? ReviewerRoleId,
    ImmutableArray<string> ScopeIds,
    string? CorrelationId);
