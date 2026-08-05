namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Captures the current non-widening role authority and resource maxima used by graph admission.</summary>
/// <param name="IsAvailable">Whether current role authority could be resolved authoritatively.</param>
/// <param name="SourceEvidenceId">The stable source evidence identity.</param>
/// <param name="RoleId">The role to which the authority applies.</param>
/// <param name="CapabilityIds">The current capability maximum.</param>
/// <param name="MaxAttempts">The current graph-wide attempt maximum.</param>
/// <param name="MaxPayloadCharacters">The current graph-wide payload maximum.</param>
/// <param name="MaxEvidenceItems">The current graph-wide evidence maximum.</param>
/// <param name="MaxResourceUnits">The current graph-wide resource-unit maximum.</param>
public sealed record GovernedLoopAuthoritySnapshot(bool IsAvailable, string SourceEvidenceId, string RoleId, IReadOnlyList<string> CapabilityIds, int MaxAttempts, int MaxPayloadCharacters, int MaxEvidenceItems, int MaxResourceUnits);
