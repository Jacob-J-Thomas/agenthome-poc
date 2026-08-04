namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Represents a non-executing authority intersection result.
/// </summary>
/// <param name="CandidateCeiling">The monotone intersection before the boundary decision is applied.</param>
/// <param name="EffectiveCeiling">The candidate ceiling only for a direct decision; otherwise a zero ceiling.</param>
/// <param name="Receipt">The bounded boundary-decision evidence.</param>
/// <param name="Validation">The structured validation result.</param>
/// <remarks>This result cannot establish trust, approval, assignment, or execution authority.</remarks>
public sealed record AuthorityIntersectionResult(
    AuthorityCeiling CandidateCeiling,
    AuthorityCeiling EffectiveCeiling,
    AuthorityBoundaryReceipt Receipt,
    AuthorityContractValidationResult Validation);
