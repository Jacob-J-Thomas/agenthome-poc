namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Describes one internal route exclusion by canonical position and route-entry digest.</summary>
/// <param name="Ordinal">The zero-based position in the canonical eligible-respondent route.</param>
/// <param name="RouteEntryHash">The lowercase SHA-256 digest of the complete canonical route entry.</param>
public sealed record HumanInputRouteExclusionIntent(int Ordinal, string RouteEntryHash);
