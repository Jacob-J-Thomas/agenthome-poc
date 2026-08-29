namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Selects one bounded page of canonical Human Input request posture.</summary>
/// <param name="MaximumCount">The requested finite page size.</param>
/// <param name="Cursor">An optional opaque continuation cursor from an unchanged canonical ledger generation.</param>
public sealed record HumanInputRequestPosturePageRequest(int MaximumCount, string? Cursor = null);
