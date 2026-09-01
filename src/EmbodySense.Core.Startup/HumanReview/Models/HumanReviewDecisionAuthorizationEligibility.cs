using System.Collections.Immutable;

namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Describes one exact reviewer role and ordered scope set admitted by a persisted Human Review request.</summary>
/// <param name="ReviewerRoleId">The exact admitted reviewer role identity.</param>
/// <param name="ScopeIds">The detached ordered scope identities required for that role.</param>
public sealed record HumanReviewDecisionAuthorizationEligibility(string ReviewerRoleId, ImmutableArray<string> ScopeIds);
