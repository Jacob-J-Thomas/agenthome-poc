using System.Collections.Immutable;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Represents the server-owned authorization outcome for one exact Human Review decision evaluation.</summary>
/// <param name="IsAuthorized">Whether authentication succeeded and the authorizer resolved canonical current actor, role, scope, and correlation facts. The decision service separately checks exact request eligibility.</param>
/// <param name="RequestHash">The exact immutable request hash echoed from the authorization request.</param>
/// <param name="DecisionOperationId">The exact operation identity echoed from the authorization request.</param>
/// <param name="ProposalHash">The exact proposal hash echoed from the authorization request.</param>
/// <param name="EvaluatedAtUtc">The exact trusted UTC evaluation instant echoed from the authorization request.</param>
/// <param name="ActorId">The authenticated actor identity when authorized.</param>
/// <param name="ReviewerRoleId">The exact current reviewer role when authorized.</param>
/// <param name="ScopeIds">The exact current ordered reviewer scopes when authorized.</param>
/// <param name="CorrelationId">The trusted authorization correlation identity when authorized.</param>
public sealed record HumanReviewDecisionAuthorization(
    bool IsAuthorized,
    string RequestHash,
    string DecisionOperationId,
    string ProposalHash,
    DateTimeOffset EvaluatedAtUtc,
    string? ActorId,
    string? ReviewerRoleId,
    ImmutableArray<string> ScopeIds,
    string? CorrelationId);
