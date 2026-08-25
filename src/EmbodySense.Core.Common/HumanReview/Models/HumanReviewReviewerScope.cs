using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one exact eligible reviewer role and its canonical ordered scope set.</summary>
/// <param name="ReviewerRoleId">The exact admitted reviewer role identity.</param>
/// <param name="ScopeIds">The non-empty ordered immutable scope IDs required for that role.</param>
public sealed partial record HumanReviewReviewerScope(string ReviewerRoleId, ImmutableArray<string> ScopeIds);
