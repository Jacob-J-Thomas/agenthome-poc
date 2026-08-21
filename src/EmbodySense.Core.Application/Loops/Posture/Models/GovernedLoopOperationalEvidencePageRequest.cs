namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Requests one deterministic finite page from an authoritative operational source.</summary>
/// <param name="MaximumCount">The positive bounded page size.</param>
/// <param name="AfterId">The optional exclusive stable identity cursor.</param>
public sealed record GovernedLoopOperationalEvidencePageRequest(int MaximumCount, string? AfterId = null);
