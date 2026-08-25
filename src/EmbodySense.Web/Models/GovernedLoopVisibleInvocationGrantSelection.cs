namespace EmbodySense.Web.Models;

/// <summary>Identifies one server-advertised exact grant choice without asserting that it remains eligible or authoritative.</summary>
/// <param name="GrantId">The stable grant identifier displayed by the current server preparation.</param>
/// <param name="Revision">The immutable grant revision displayed by the current server preparation.</param>
/// <param name="ContentHash">The exact grant content hash displayed by the current server preparation.</param>
public sealed record GovernedLoopVisibleInvocationGrantSelection(string GrantId, int Revision, string ContentHash);
