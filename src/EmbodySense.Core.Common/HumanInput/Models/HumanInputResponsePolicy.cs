using System.Collections.Immutable;

namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Defines the explicit selection policy for response data; it grants no authority and performs no delivery or persistence.
/// </summary>
/// <param name="Kind">The supported response-selection policy.</param>
/// <param name="RequiredResponseCount">The matching quorum count, or the merge contributor threshold; otherwise null.</param>
/// <param name="OrderedRoleIds">The authored required, contributor, or selector role order for named-role, merge, or manual selection; otherwise null.</param>
public sealed record HumanInputResponsePolicy(HumanInputResponsePolicyKind Kind, int? RequiredResponseCount, ImmutableArray<string>? OrderedRoleIds);
